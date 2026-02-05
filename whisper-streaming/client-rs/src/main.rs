use anyhow::{Context, Result};
use base64::Engine;
use clap::Parser;
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use futures_util::{SinkExt, StreamExt};
use serde::{Deserialize, Serialize};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc;
use tokio_tungstenite::{connect_async, tungstenite::Message};
use webrtc_vad::Vad;

#[derive(Parser, Debug)]
#[command(name = "whisper-client", about = "Streaming speech-to-text client")]
struct Args {
    #[arg(long, env = "SERVER_URL", default_value = "ws://localhost:8765")]
    server_url: String,

    #[arg(long, env = "STRATEGY", default_value = "prompt")]
    strategy: String,

    #[arg(long, env = "MIN_ENERGY", default_value = "0.01")]
    min_energy: f32,

    #[arg(long, default_value = "16000")]
    sample_rate: u32,

    #[arg(long, default_value = "300")]
    silence_threshold_ms: u32,

    #[arg(long, default_value = "5000")]
    max_speech_ms: u32,

    #[arg(long, default_value = "3")]
    onset_threshold: u32,
}

#[derive(Default)]
struct SpeechState {
    is_speaking: bool,
    silence_count: u32,
    onset_count: u32,
    energy_sum: f32,
    chunk_count: u32,
}

impl SpeechState {
    fn reset(&mut self) {
        self.is_speaking = false;
        self.silence_count = 0;
        self.onset_count = 0;
        self.energy_sum = 0.0;
        self.chunk_count = 0;
    }

    fn start_speaking(&mut self) {
        self.is_speaking = true;
    }

    fn add_chunk(&mut self, energy: f32) {
        self.energy_sum += energy;
        self.chunk_count += 1;
    }

    fn avg_energy(&self) -> f32 {
        if self.chunk_count == 0 {
            0.0
        } else {
            self.energy_sum / self.chunk_count as f32
        }
    }

    fn duration_ms(&self, chunk_ms: u32) -> u32 {
        self.chunk_count * chunk_ms
    }
}

#[derive(Serialize)]
struct AudioFrame {
    #[serde(rename = "type")]
    msg_type: &'static str,
    audio: String,
    sample_rate: u32,
}

#[derive(Serialize)]
struct VadEnd {
    #[serde(rename = "type")]
    msg_type: &'static str,
}

#[derive(Deserialize)]
struct ServerResponse {
    #[serde(rename = "type")]
    msg_type: String,
    text: Option<String>,
    processing_time_ms: Option<f64>,
}

struct LatencyStats {
    server_times: Vec<f64>,
}

impl LatencyStats {
    fn new() -> Self {
        Self { server_times: Vec::new() }
    }

    fn record(&mut self, ms: f64) {
        self.server_times.push(ms);
    }

    fn summary(&self) -> String {
        if self.server_times.is_empty() {
            "No data".to_string()
        } else {
            let n = self.server_times.len();
            let avg: f64 = self.server_times.iter().sum::<f64>() / n as f64;
            format!("Transcriptions: {} | Avg server time: {:.0}ms", n, avg)
        }
    }
}

fn calculate_energy(samples: &[f32]) -> f32 {
    if samples.is_empty() {
        return 0.0;
    }
    let sum_sq: f32 = samples.iter().map(|&s| s * s).sum();
    (sum_sq / samples.len() as f32).sqrt()
}

fn f32_to_i16(samples: &[f32]) -> Vec<i16> {
    samples
        .iter()
        .map(|&s| (s * 32768.0).clamp(-32768.0, 32767.0) as i16)
        .collect()
}

fn build_audio_frame(audio: &[f32], sample_rate: u32) -> String {
    let bytes: Vec<u8> = audio
        .iter()
        .flat_map(|&s| s.to_le_bytes())
        .collect();
    let b64 = base64::engine::general_purpose::STANDARD.encode(&bytes);
    serde_json::to_string(&AudioFrame {
        msg_type: "audio_frame",
        audio: b64,
        sample_rate,
    })
    .unwrap()
}

fn build_vad_end() -> String {
    serde_json::to_string(&VadEnd { msg_type: "vad_end" }).unwrap()
}

fn clear_line(len: usize) {
    print!("\r{}\r", " ".repeat(len + 20));
}

fn resample(samples: &[f32], from_rate: u32, to_rate: u32) -> Vec<f32> {
    if from_rate == to_rate {
        return samples.to_vec();
    }
    let ratio = to_rate as f64 / from_rate as f64;
    let output_len = (samples.len() as f64 * ratio) as usize;
    (0..output_len)
        .map(|i| {
            let src_idx = i as f64 / ratio;
            let idx = src_idx.floor() as usize;
            let frac = src_idx.fract() as f32;
            if idx + 1 < samples.len() {
                samples[idx] * (1.0 - frac) + samples[idx + 1] * frac
            } else if idx < samples.len() {
                samples[idx]
            } else {
                0.0
            }
        })
        .collect()
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();

    let ws_url = format!("{}/ws/transcribe/{}", args.server_url, args.strategy);
    let chunk_ms: u32 = 30;
    let _chunk_size = (args.sample_rate * chunk_ms / 1000) as usize;
    let silence_chunks = args.silence_threshold_ms / chunk_ms;

    println!("Server: {}", ws_url);
    println!("Strategy: {}", args.strategy);
    println!("Min energy: {}", args.min_energy);
    println!("Press Ctrl+C to stop\n");

    // Audio capture channel
    let (audio_tx, mut audio_rx) = mpsc::channel::<Vec<f32>>(100);

    // Start audio capture
    let host = cpal::default_host();
    let device = host
        .default_input_device()
        .context("No input device available")?;

    // Use device's default config, we'll handle sample rate conversion if needed
    let default_config = device.default_input_config()?;
    let device_sample_rate = default_config.sample_rate().0;

    let config = cpal::StreamConfig {
        channels: 1,
        sample_rate: cpal::SampleRate(device_sample_rate),
        buffer_size: cpal::BufferSize::Default,
    };

    // Calculate chunk size at device sample rate, then we'll resample
    let device_chunk_size = (device_sample_rate * chunk_ms / 1000) as usize;
    println!("Device sample rate: {}Hz (target: {}Hz)", device_sample_rate, args.sample_rate);

    let running = Arc::new(AtomicBool::new(true));
    let running_clone = running.clone();

    let stream = device.build_input_stream(
        &config,
        move |data: &[f32], _: &cpal::InputCallbackInfo| {
            if running_clone.load(Ordering::Relaxed) {
                let _ = audio_tx.blocking_send(data.to_vec());
            }
        },
        |err| eprintln!("Audio error: {}", err),
        None,
    )?;

    stream.play()?;

    // VAD setup
    let mut vad = Vad::new_with_rate_and_mode(
        webrtc_vad::SampleRate::Rate16kHz,
        webrtc_vad::VadMode::Aggressive,
    );

    // Connection state
    let mut ws_stream: Option<
        futures_util::stream::SplitSink<
            tokio_tungstenite::WebSocketStream<
                tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
            >,
            Message,
        >,
    > = None;
    let mut ws_read: Option<
        futures_util::stream::SplitStream<
            tokio_tungstenite::WebSocketStream<
                tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
            >,
        >,
    > = None;

    // Try initial connection
    match connect_async(&ws_url).await {
        Ok((stream, _)) => {
            println!("[connected] Server connected");
            let (write, read) = stream.split();
            ws_stream = Some(write);
            ws_read = Some(read);
        }
        Err(_) => {
            println!("[offline] Server not available, will retry");
            println!("[offline] Audio capture active, speech detection running\n");
        }
    }

    let mut state = SpeechState::default();
    let mut stats = LatencyStats::new();
    let mut current_partial = String::new();
    let mut reconnect_timer = tokio::time::interval(Duration::from_secs(5));
    let mut audio_buffer: Vec<f32> = Vec::with_capacity(device_chunk_size * 2);

    // Main loop
    loop {
        tokio::select! {
            // Handle Ctrl+C
            _ = tokio::signal::ctrl_c() => {
                running.store(false, Ordering::Relaxed);
                break;
            }

            // Reconnect timer
            _ = reconnect_timer.tick(), if ws_stream.is_none() => {
                if let Ok((stream, _)) = connect_async(&ws_url).await {
                    println!("[connected] Server connected");
                    let (write, read) = stream.split();
                    ws_stream = Some(write);
                    ws_read = Some(read);
                }
            }

            // Handle server responses
            msg = async {
                if let Some(ref mut read) = ws_read {
                    read.next().await
                } else {
                    std::future::pending().await
                }
            } => {
                match msg {
                    Some(Ok(Message::Text(text))) => {
                        if let Ok(resp) = serde_json::from_str::<ServerResponse>(&text) {
                            let text_content = resp.text.unwrap_or_default().trim().to_string();
                            if resp.msg_type == "partial" && !text_content.is_empty() {
                                clear_line(current_partial.len());
                                print!("[partial] {}", text_content);
                                use std::io::Write;
                                std::io::stdout().flush().ok();
                                current_partial = text_content;
                            } else if resp.msg_type == "final" {
                                let ms = resp.processing_time_ms.unwrap_or(0.0);
                                stats.record(ms);
                                if !text_content.is_empty() {
                                    clear_line(current_partial.len());
                                    println!("[final {:.0}ms] {}", ms, text_content);
                                    current_partial.clear();
                                }
                            }
                        }
                    }
                    Some(Err(_)) | None => {
                        if ws_stream.is_some() {
                            println!("\n[disconnected] Server connection lost");
                            ws_stream = None;
                            ws_read = None;
                        }
                    }
                    _ => {}
                }
            }

            // Handle audio from device
            Some(samples) = audio_rx.recv() => {
                audio_buffer.extend_from_slice(&samples);

                // Process complete chunks at device sample rate
                while audio_buffer.len() >= device_chunk_size {
                    let device_chunk: Vec<f32> = audio_buffer.drain(..device_chunk_size).collect();

                    // Resample to target rate for VAD and server
                    let chunk = resample(&device_chunk, device_sample_rate, args.sample_rate);

                    // VAD + energy detection
                    let i16_samples = f32_to_i16(&chunk);
                    let vad_speech = vad.is_voice_segment(&i16_samples).unwrap_or(false);
                    let energy = calculate_energy(&chunk);
                    let speech_detected = vad_speech && energy >= args.min_energy;

                    // Handle speech onset (debounce)
                    if speech_detected {
                        state.silence_count = 0;
                        if !state.is_speaking {
                            state.onset_count += 1;
                            if state.onset_count >= args.onset_threshold {
                                state.start_speaking();
                            }
                        }
                    } else {
                        state.onset_count = 0;
                    }

                    // Send audio during speech
                    if state.is_speaking {
                        if let Some(ref mut ws) = ws_stream {
                            let msg = build_audio_frame(&chunk, args.sample_rate);
                            if ws.send(Message::Text(msg)).await.is_err() {
                                println!("\n[disconnected] Server connection lost");
                                ws_stream = None;
                                ws_read = None;
                            }
                        }
                        state.add_chunk(energy);
                    }

                    // Check for finalization
                    let mut should_finalize = false;
                    if state.is_speaking {
                        if !speech_detected {
                            state.silence_count += 1;
                            if state.silence_count >= silence_chunks {
                                should_finalize = true;
                            }
                        }
                        if state.duration_ms(chunk_ms) >= args.max_speech_ms {
                            should_finalize = true;
                        }
                    }

                    if should_finalize {
                        if state.avg_energy() >= args.min_energy {
                            if let Some(ref mut ws) = ws_stream {
                                let msg = build_vad_end();
                                if ws.send(Message::Text(msg)).await.is_err() {
                                    ws_stream = None;
                                    ws_read = None;
                                }
                            } else if state.chunk_count > 0 {
                                let duration = state.duration_ms(chunk_ms);
                                println!("[offline] Speech detected ({}ms) - server unavailable", duration);
                            }
                        }
                        state.reset();
                    }
                }
            }
        }
    }

    drop(stream);
    println!("\n--- Latency Summary ---");
    println!("{}", stats.summary());

    Ok(())
}
