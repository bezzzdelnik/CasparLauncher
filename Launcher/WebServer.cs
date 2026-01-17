using System.Net;
using System.Text;
using System.Text.Json;

namespace CasparLauncher.Launcher;

public class WebServer : IDisposable
{
    private HttpListener? _listener;
    private Thread? _serverThread;
    private bool _isRunning;
    private CancellationTokenSource _cancellationTokenSource = new();

    public int Port { get; set; } = 8080;
    public List<string> AllowedAddresses { get; set; } = new() { "localhost", "127.0.0.1" };
    public bool IsRunning => _isRunning;

    public void Start()
    {
        if (_isRunning) return;

        // Убеждаемся, что предыдущий экземпляр полностью остановлен
        if (_listener != null || _serverThread != null)
        {
            Stop();
            // Даем время потоку завершиться
            Thread.Sleep(100);
        }

        try
        {
            // Если был остановлен, создаем новый CancellationTokenSource
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
            }

            _listener = new HttpListener();
            
            // Добавляем префиксы для всех разрешенных адресов
            foreach (var address in AllowedAddresses)
            {
                if (address == "*")
                {
                    // Для "*" используем + для прослушивания всех адресов
                    _listener.Prefixes.Add($"http://+:{Port}/");
                }
                else
                {
                    var prefix = $"http://{address}:{Port}/";
                    _listener.Prefixes.Add(prefix);
                }
            }

            _listener.Start();
            _isRunning = true;

            _serverThread = new Thread(async () => await ListenAsync(_cancellationTokenSource.Token))
            {
                IsBackground = true
            };
            _serverThread.Start();
        }
        catch (Exception ex)
        {
            var errorMsg = L.ResourceManager.GetString("Launchpad_WebServerStartError", L.Culture) ?? $"Web server start error: {ex.Message}";
            Debug.WriteLine(errorMsg);
            _isRunning = false;
            
            // Очищаем ресурсы при ошибке
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // Игнорируем ошибки при очистке
            }
            _listener = null;
            
            throw;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false; // Устанавливаем флаг первым, чтобы поток мог завершиться

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch
        {
            // Игнорируем ошибки при отмене
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Игнорируем ошибки при остановке
        }

        try
        {
            _listener?.Close();
        }
        catch
        {
            // Игнорируем ошибки при закрытии
        }

        _listener = null;

        // Ждем завершения потока
        try
        {
            _serverThread?.Join(2000); // Ждем максимум 2 секунды
        }
        catch
        {
            // Игнорируем ошибки
        }

        _serverThread = null;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_listener == null || !_listener.IsListening) break;

                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequest(context), cancellationToken);
            }
            catch (HttpListenerException)
            {
                // Сервер остановлен
                break;
            }
            catch (ObjectDisposedException)
            {
                // Объект уже удален
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка при обработке запроса: {ex.Message}");
            }
        }
    }

    private async Task ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // Проверка разрешенных адресов
            if (!IsAddressAllowed(request.RemoteEndPoint?.Address))
            {
                response.StatusCode = 403;
                response.Close();
                return;
            }

            // CORS заголовки
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            // Обработка маршрутов
            if (path == "/" || path == "/index.html")
            {
                await HandleWebPage(response);
            }
            else if (path == "/api/executables" && method == "GET")
            {
                await HandleGetExecutables(response);
            }
            else if (path.StartsWith("/api/executables/") && method == "POST")
            {
                var parts = path.Split('/');
                if (parts.Length >= 4)
                {
                    var idStr = parts[3];
                    if (int.TryParse(idStr, out var id))
                    {
                        var action = request.QueryString["action"] ?? "start";
                        await HandleExecutableAction(response, id, action);
                    }
                    else
                    {
                        response.StatusCode = 400;
                        await WriteResponse(response, "Invalid executable ID");
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteResponse(response, "Invalid request path");
                }
            }
            else
            {
                response.StatusCode = 404;
                await WriteResponse(response, "Not Found");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка обработки запроса: {ex.Message}");
            response.StatusCode = 500;
            await WriteResponse(response, $"Internal Server Error: {ex.Message}");
        }
        finally
        {
            response.Close();
        }
    }

    private bool IsAddressAllowed(IPAddress? address)
    {
        if (address == null) return false;

        // Разрешаем localhost и 127.0.0.1 всегда
        if (IPAddress.IsLoopback(address)) return true;

        // Проверяем разрешенные адреса
        foreach (var allowedAddr in AllowedAddresses)
        {
            if (allowedAddr == "*") return true; // Разрешить все адреса

            if (IPAddress.TryParse(allowedAddr, out var allowedIp))
            {
                if (address.Equals(allowedIp)) return true;
            }
            else
            {
                // Проверка по имени хоста
                try
                {
                    var hostEntry = Dns.GetHostEntry(allowedAddr);
                    if (hostEntry.AddressList.Any(ip => ip.Equals(address)))
                        return true;
                }
                catch
                {
                    // Игнорируем ошибки разрешения имени
                }
            }
        }

        return false;
    }

    private async Task HandleWebPage(HttpListenerResponse response)
    {
        var html = GetWebPageHtml();
        response.ContentType = "text/html; charset=utf-8";
        response.StatusCode = 200;
        await WriteResponse(response, html);
    }

    private const string WebPageCss = @"* {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
}
body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    min-height: 100vh;
    padding: 20px;
}
.container {
    max-width: 1200px;
    margin: 0 auto;
}
.header {
    background: white;
    padding: 30px;
    border-radius: 10px;
    box-shadow: 0 10px 30px rgba(0,0,0,0.2);
    margin-bottom: 30px;
    text-align: center;
}
.header h1 {
    color: #333;
    margin-bottom: 10px;
}
.header p {
    color: #666;
}
.executables-list {
    display: grid;
    gap: 20px;
}
.executable-card {
    background: white;
    padding: 25px;
    border-radius: 10px;
    box-shadow: 0 5px 15px rgba(0,0,0,0.1);
    display: flex;
    justify-content: space-between;
    align-items: center;
    transition: transform 0.2s, box-shadow 0.2s;
}
.executable-card:hover {
    transform: translateY(-2px);
    box-shadow: 0 8px 20px rgba(0,0,0,0.15);
}
.executable-info {
    flex: 1;
}
.executable-name {
    font-size: 20px;
    font-weight: bold;
    color: #333;
    margin-bottom: 5px;
}
.executable-path {
    font-size: 14px;
    color: #666;
    font-family: 'Courier New', monospace;
    margin-bottom: 10px;
}
.executable-status {
    display: inline-block;
    padding: 5px 15px;
    border-radius: 20px;
    font-size: 12px;
    font-weight: bold;
}
.status-running {
    background: #4CAF50;
    color: white;
}
.status-stopped {
    background: #f44336;
    color: white;
}
.status-not-found {
    background: #ff9800;
    color: white;
}
.executable-actions {
    display: flex;
    gap: 10px;
}
.btn {
    padding: 10px 20px;
    border: none;
    border-radius: 5px;
    cursor: pointer;
    font-size: 14px;
    font-weight: bold;
    transition: all 0.2s;
    text-transform: uppercase;
}
.btn:hover {
    transform: scale(1.05);
}
.btn:active {
    transform: scale(0.95);
}
.btn-start {
    background: #4CAF50;
    color: white;
}
.btn-start:hover {
    background: #45a049;
}
.btn-stop {
    background: #f44336;
    color: white;
}
.btn-stop:hover {
    background: #da190b;
}
.btn-restart {
    background: #2196F3;
    color: white;
}
.btn-restart:hover {
    background: #0b7dda;
}
.btn:disabled {
    background: #ccc;
    cursor: not-allowed;
    transform: none;
}
.loading {
    text-align: center;
    padding: 50px;
    color: white;
    font-size: 18px;
}
.error {
    background: #f44336;
    color: white;
    padding: 15px;
    border-radius: 5px;
    margin-bottom: 20px;
    display: none;
}
.success {
    background: #4CAF50;
    color: white;
    padding: 15px;
    border-radius: 5px;
    margin-bottom: 20px;
    display: none;
}
.refresh-btn {
    position: fixed;
    bottom: 30px;
    right: 30px;
    background: white;
    color: #667eea;
    border: 2px solid #667eea;
    padding: 15px 25px;
    border-radius: 50px;
    cursor: pointer;
    font-weight: bold;
    box-shadow: 0 5px 15px rgba(0,0,0,0.2);
    transition: all 0.2s;
}
.refresh-btn:hover {
    background: #667eea;
    color: white;
    transform: scale(1.1);
}";

    private string EscapeJsString(string str)
    {
        return JsonSerializer.Serialize(str);
    }
    
    private string GetWebPageHtml()
    {
        var lang = L.Culture?.TwoLetterISOLanguageName ?? "en";
        var title = L.ResourceManager.GetString("WebServer_Title", L.Culture) ?? "Application Management";
        var loading = L.ResourceManager.GetString("WebServer_Loading", L.Culture) ?? "Loading...";
        var refresh = L.ResourceManager.GetString("WebServer_Refresh", L.Culture) ?? "Refresh";
        var errorLoadingData = L.ResourceManager.GetString("WebServer_ErrorLoadingData", L.Culture) ?? "Error loading data";
        var noApplications = L.ResourceManager.GetString("WebServer_NoApplications", L.Culture) ?? "No applications to display";
        var notFound = L.ResourceManager.GetString("WebServer_NotFound", L.Culture) ?? "Not found";
        var running = L.ResourceManager.GetString("WebServer_Running", L.Culture) ?? "Running";
        var stopped = L.ResourceManager.GetString("WebServer_Stopped", L.Culture) ?? "Stopped";
        var pathNotSpecified = L.ResourceManager.GetString("WebServer_PathNotSpecified", L.Culture) ?? "Path not specified";
        var start = L.ResourceManager.GetString("WebServer_Start", L.Culture) ?? "Start";
        var stop = L.ResourceManager.GetString("WebServer_Stop", L.Culture) ?? "Stop";
        var restart = L.ResourceManager.GetString("WebServer_Restart", L.Culture) ?? "Restart";
        var errorPrefix = L.ResourceManager.GetString("WebServer_Error", L.Culture) ?? "Error: ";
        
        return $@"<!DOCTYPE html>
<html lang=""{lang}"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>CasparLauncher - {title}</title>
    <style>
{WebPageCss}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h1>🚀 CasparLauncher</h1>
            <p>{title}</p>
        </div>
        <div id=""error"" class=""error"" style=""display: none;""></div>
        <div id=""success"" class=""success""></div>
        <div id=""loading"" class=""loading"">{loading}</div>
        <div id=""executables"" class=""executables-list""></div>
    </div>
    <button class=""refresh-btn"" onclick=""loadExecutables()"">🔄 {refresh}</button>
    <script>
        var strings = {{
            loading: {EscapeJsString(loading)},
            errorLoadingData: {EscapeJsString(errorLoadingData)},
            noApplications: {EscapeJsString(noApplications)},
            notFound: {EscapeJsString(notFound)},
            running: {EscapeJsString(running)},
            stopped: {EscapeJsString(stopped)},
            pathNotSpecified: {EscapeJsString(pathNotSpecified)},
            start: {EscapeJsString(start)},
            stop: {EscapeJsString(stop)},
            restart: {EscapeJsString(restart)},
            errorPrefix: {EscapeJsString(errorPrefix)}
        }};
        
        async function loadExecutables() {{
            var loading = document.getElementById('loading');
            var executablesDiv = document.getElementById('executables');
            var errorDiv = document.getElementById('error');
            var successDiv = document.getElementById('success');
            
            loading.style.display = 'block';
            executablesDiv.innerHTML = '';
            errorDiv.style.display = 'none';
            successDiv.style.display = 'none';
            
            try {{
                var response = await fetch('/api/executables');
                if (!response.ok) throw new Error(strings.errorLoadingData);
                
                var executables = await response.json();
                loading.style.display = 'none';
                
                if (executables.length === 0) {{
                    executablesDiv.innerHTML = '<div class=""executable-card""><p>' + strings.noApplications + '</p></div>';
                    return;
                }}
                
                executablesDiv.innerHTML = executables.map(ex => {{
                    var statusClass = !ex.exists ? 'status-not-found' : (ex.isRunning ? 'status-running' : 'status-stopped');
                    var statusText = !ex.exists ? strings.notFound : (ex.isRunning ? strings.running : strings.stopped);
                    
                    return `
                        <div class=""executable-card"">
                            <div class=""executable-info"">
                                <div class=""executable-name"">${{escapeHtml(ex.name)}}</div>
                                <div class=""executable-path"">${{escapeHtml(ex.path || strings.pathNotSpecified)}}</div>
                                <span class=""executable-status ${{statusClass}}"">${{statusText}}</span>
                            </div>
                            <div class=""executable-actions"">
                                <button class=""btn btn-start"" onclick=""execAction(${{ex.id}}, 'start')"" 
                                    ${{ex.isRunning || !ex.exists ? 'disabled' : ''}}>▶ ${{strings.start}}</button>
                                <button class=""btn btn-stop"" onclick=""execAction(${{ex.id}}, 'stop')"" 
                                    ${{!ex.isRunning || !ex.exists ? 'disabled' : ''}}>⏹ ${{strings.stop}}</button>
                                <button class=""btn btn-restart"" onclick=""execAction(${{ex.id}}, 'restart')"" 
                                    ${{!ex.isRunning || !ex.exists ? 'disabled' : ''}}>🔄 ${{strings.restart}}</button>
                            </div>
                        </div>
                    `;
                }}).join('');
            }} catch (error) {{
                loading.style.display = 'none';
                errorDiv.textContent = strings.errorPrefix + error.message;
                errorDiv.style.display = 'block';
            }}
        }}
        
        async function execAction(id, action) {{
            var errorDiv = document.getElementById('error');
            var successDiv = document.getElementById('success');
            
            errorDiv.style.display = 'none';
            successDiv.style.display = 'none';
            
            try {{
                var response = await fetch(`/api/executables/${{id}}?action=${{action}}`, {{
                    method: 'POST'
                }});
                
                var result = await response.json();
                
                if (result.success) {{
                    successDiv.textContent = result.message;
                    successDiv.style.display = 'block';
                    setTimeout(() => {{
                        successDiv.style.display = 'none';
                    }}, 3000);
                    await loadExecutables();
                }} else {{
                    errorDiv.textContent = strings.errorPrefix + result.message;
                    errorDiv.style.display = 'block';
                }}
            }} catch (error) {{
                errorDiv.textContent = strings.errorPrefix + error.message;
                errorDiv.style.display = 'block';
            }}
        }}
        
        function escapeHtml(text) {{
            var div = document.createElement(""div"");
            div.textContent = text;
            return div.innerHTML;
        }}
        
        // Автообновление каждые 5 секунд
        loadExecutables();
        setInterval(loadExecutables, 5000);
    </script>
</body>
</html>";
    }

    private async Task HandleGetExecutables(HttpListenerResponse response)
    {
        var executables = App.Launchpad.Executables.Select((ex, index) => new
        {
            id = index + 1,
            name = ex.Name,
            path = ex.Path,
            isRunning = ex.IsRunning,
            autoStart = ex.AutoStart,
            exists = ex.Exists
        }).ToList();

        var json = JsonSerializer.Serialize(executables, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        response.ContentType = "application/json; charset=utf-8";
        response.StatusCode = 200;
        await WriteResponse(response, json);
    }

    private async Task HandleExecutableAction(HttpListenerResponse response, int id, string action)
    {
        if (id < 1 || id > App.Launchpad.Executables.Count)
        {
            response.StatusCode = 404;
            await WriteResponse(response, "Executable not found");
            return;
        }

        var executable = App.Launchpad.Executables[id - 1];

        try
        {
            switch (action.ToLower())
            {
                case "start":
                    Launchpad.Start(executable);
                    await WriteResponse(response, JsonSerializer.Serialize(new { success = true, message = "Executable started" }));
                    break;

                case "stop":
                    Launchpad.Stop(executable);
                    await WriteResponse(response, JsonSerializer.Serialize(new { success = true, message = "Executable stopped" }));
                    break;

                case "restart":
                    Launchpad.Restart(executable);
                    await WriteResponse(response, JsonSerializer.Serialize(new { success = true, message = "Executable restarted" }));
                    break;

                default:
                    response.StatusCode = 400;
                    await WriteResponse(response, JsonSerializer.Serialize(new { success = false, message = "Invalid action" }));
                    break;
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            await WriteResponse(response, JsonSerializer.Serialize(new { success = false, message = ex.Message }));
        }
    }

    private async Task WriteResponse(HttpListenerResponse response, string content)
    {
        var buffer = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }

    public void Dispose()
    {
        Stop();
        _cancellationTokenSource?.Dispose();
    }
}

