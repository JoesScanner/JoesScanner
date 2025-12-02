# JoesScanner App

JoesScanner is a cross-platform application for listening to live radio calls from the Joe’s Scanner service or any compatible Trunking Recorder server. It provides clean UI presentation, reliable audio streaming, and automatic call queue handling. The app is built for continuous, long-term monitoring on Windows, Android, and iOS.

---

## What the App Does

- Streams radio calls in real time  
- Displays upcoming calls in a queue  
- Allows you to jump directly to live activity  
- Provides playback speed control  
- Shows connection status at a glance  
- Connects to Joe’s Scanner or your own server  
- Automatically saves your preferences  
- Includes a built-in log viewer for troubleshooting  

---

## Using With Joe’s Scanner

Subscribers can connect directly to the Joe’s Scanner service.

1. Open the Settings page  
2. Enter the server URL  
3. Provide your username and password  
4. Test and save your connection  

Once authenticated, calls will begin loading and playing automatically.

---

## Using Your Own Trunking Recorder Server

The app can also connect to self-hosted Trunking Recorder (TR) servers.

You will need:

- Your server URL  
- A username  
- A password (if your TR server uses Basic Auth)  

Enter these on the Settings screen and use “Test Connection.” When the connection succeeds, the app will begin displaying your call list and streaming audio.

---

## Supported Platforms

- Windows  
- Android  
- iOS  
- macOS  

The application runs on desktops, laptops, tablets, and mobile devices.

---

## Basic Setup

1. Install the app for your platform  
2. Open the Settings page  
3. Enter your server URL  
4. (Optional) Enter username and password  
5. Select “Test Connection”  
6. Save your settings and return to the main screen  

When configured correctly, call activity will appear automatically.

---

## Features

### Call Queue  
Incoming calls are queued and played in sequence, with the ability to jump ahead to live activity.

### Audio Controls  
- Play the most recent call  
- Jump to live  
- Adjust playback speed for clearing backlogs  

### Connection Status  
A badge indicates whether the app is connected, connecting, or offline.

### Logging  
The log viewer helps diagnose server or connection issues. Logs can be exported for support.

---

## Troubleshooting

If calls are not displaying or audio does not play:

- Confirm the server URL is correct  
- Verify username and password  
- Use the “Test Connection” button  
- Check the log viewer for errors  
- Ensure your network can reach the server  
- For TR servers, confirm that the Call History API is enabled and reachable  

---

## Downloads

The latest builds for each platform are available under GitHub Releases:  
https://github.com/JoesScanner/JoesScanner/releases

---

## License

The JoesScanner app is provided under a dual license:

- Free for personal, non-commercial use  
- Commercial use requires a paid license  

See the LICENSE file for full terms.

---

## Support

Joe’s Scanner subscribers:  
https://www.joesscanner.com

Commercial licensing and technical support:  
support@joesscanner.com
