# JoesScanner

JoesScanner is a cross-platform .NET MAUI application for streaming and managing radio calls from the Joe’s Scanner service or from any compatible Trunking Recorder server. The application is designed for long-term continuous listening with a clean interface and reliable audio handling.

---

## Overview

JoesScanner connects to the Joe’s Scanner servers by default, but users may also point the application to their own Trunking Recorder installation by entering a custom server URL and optional Basic Auth credentials.

The application provides:
- Live call playback  
- Automatic call queue processing  
- Call details and metadata  
- Device-optimized layouts  
- Configurable settings for server connection and authentication  

Runs on:
- Windows  
- Android  
- iOS  
- macOS  

---

## Features

- Connects to Joe’s Scanner servers or custom Trunking Recorder servers  
- Live audio streaming  
- Automatic call queue handling  
- Basic Auth support  
- Playback speed adjustment  
- Connection status indicator  
- Configurable server URL  
- Secure credential storage  
- Rolling log file with export  
- Light and dark theme support  
- Error and connection diagnostics  

---

## Requirements

- .NET 9 SDK  
- Visual Studio with the MAUI workload installed  
- A Joe’s Scanner server endpoint or a Trunking Recorder server endpoint  
- Supported operating systems: Windows, Android, iOS, macOS  

---

## Building

1. Clone the repository:  
   ```bash
   git clone https://github.com/JoesScanner/JoesScanner.git
