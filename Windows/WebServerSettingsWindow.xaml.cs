namespace CasparLauncher;

public partial class WebServerSettingsWindow : DialogWindow
{
    public WebServerSettingsWindow()
    {
        try
        {
            // Убеждаемся, что WebServer инициализирован ДО загрузки XAML
            if (App.Launchpad.WebServer == null)
            {
                App.Launchpad.InitializeWebServer();
            }
            
            // Убеждаемся, что свойства инициализированы
            _ = App.Launchpad.WebServerStatusText;
            _ = App.Launchpad.WebServerUrl;
            
            InitializeComponent();
        }
        catch (Exception ex)
        {
            var errorFormat = L.ResourceManager.GetString("WebServerSettings_InitializationError", L.Culture) ?? 
                "Error initializing web server settings window: {0}\n\n{1}";
            MessageBox.Show(string.Format(errorFormat, ex.Message, ex.StackTrace), 
                L.CreateConfigFileErrorCaption, MessageBoxButton.OK, MessageBoxImage.Error);
            Debug.WriteLine($"Error WebServerSettingsWindow: {ex}");
            throw;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

