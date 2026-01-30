using JoesScanner.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JoesScanner;

public partial class AppShell : Shell
{
    private readonly IServiceProvider _services;

    public AppShell(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        InitializeComponent();

        // Single-root shell: everything is hosted inside RootPage.
        Items.Clear();

        Items.Add(new ShellContent
        {
            Route = "root",
            Title = string.Empty,
            ContentTemplate = new DataTemplate(() => _services.GetRequiredService<RootPage>())
        });
    }
}
