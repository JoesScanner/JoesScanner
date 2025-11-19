JoesScanner MAUI bootstrap

This bundle contains ViewModels, services, and pages for the features you listed:
- Continuous call stream
- Display of transcriptions with header info
- Settings for server and playback
- Donate link
- Audio caching stub and playback stub

Folders:
- Models
- ViewModels
- Services
- Views
- README_JoesScannerBootstrap.txt


How to add this to your existing JoesScanner project

1. Backup existing files
   - In your solution explorer note any existing files with the same names:
     - Views/MainPage.xaml and MainPage.xaml.cs
     - Any custom ViewModels or services you already added
   - If you changed your template MainPage, copy it somewhere safe first.

2. Copy the files
   - Extract the zip.
   - Copy the Models, ViewModels, Services, and Views folders into:
     C:\Users\nate\source\repos\JoesScanner\JoesScanner\
   - Allow it to merge folders. If Windows asks about overwriting MainPage.xaml and MainPage.xaml.cs,
     choose overwrite only if you are ok with replacing the old default page.

3. Wire up DI in MauiProgram
   - Open MauiProgram.cs.
   - At the top add:
       using JoesScanner.Services;
   - In CreateMauiApp, after UseMauiApp<App>() call ConfigureJoesScanner like this:
       var builder = MauiApp.CreateBuilder();
       builder
           .UseMauiApp<App>()
           .ConfigureJoesScanner()
           .ConfigureFonts(fonts =>
           {
               fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
               fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
           });

       return builder.Build();

4. Hook pages into Shell
   - Open AppShell.xaml.
   - At the top add:
       xmlns:views="clr-namespace:JoesScanner.Views"
   - Replace the default ShellContent with something like:
       <TabBar>
         <ShellContent
             Title="Live"
             ContentTemplate="{DataTemplate views:MainPage}" />

         <ShellContent
             Title="Settings"
             ContentTemplate="{DataTemplate views:SettingsPage}" />
       </TabBar>

5. Build and run
   - Clean and rebuild the solution.
   - Run on Windows first.
   - You should see a list view that slowly fills with sample calls created by CallStreamService.
   - The donate button should open your browser to https://www.joesscanner.com/donate.
   - Settings page should store ServerUrl and AutoPlay in MAUI Preferences.

6. Replace the placeholder call stream and audio playback
   - CallStreamService currently returns fake data and an empty AudioUrl.
     Replace the loop in GetCallStreamAsync with your real JAAS API or websocket client.
   - AudioPlaybackService currently logs calls but does not play audio yet.
     Implement playback using MediaElement or platform specific APIs when you are ready.
