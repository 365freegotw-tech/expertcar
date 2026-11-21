using System.IO;
using System.Windows;
using ExpertCar.App.Presentation;
using ExpertCar.Core;
using ExpertCar.Import;

namespace ExpertCar.App;

public partial class App : Application
{
    private ImportV02Service? storage;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("zh-TW");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-TW");
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("zh-TW")));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            storage = await ExpertCarServices.OpenAsync(AppDataPath.DatabasePath());
            IExpertCarV02Service service = storage;
            var preferences = new JsonAppPreferencesStore(Path.Combine(Path.GetDirectoryName(AppDataPath.DatabasePath())!, "ui-preferences-v02.json"));
            var viewModel = new WorkspaceV02ViewModel(service, storage, preferences);
            MainWindow = new MainWindow(viewModel, storage); ShutdownMode = ShutdownMode.OnMainWindowClose; MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(UiText.Error(ex), "無法啟動汽修賺錢專家", MessageBoxButton.OK, MessageBoxImage.Warning); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { storage?.Dispose(); base.OnExit(e); }
}
