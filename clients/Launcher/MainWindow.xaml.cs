using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MuVoidLauncher
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Modelos para versión.json (publicado en GitHub Releases)
    // ─────────────────────────────────────────────────────────────────────────

    public record UpdateFile(
        [property: JsonPropertyName("path")]     string Path,
        [property: JsonPropertyName("sha256")]   string Sha256,
        [property: JsonPropertyName("size")]     long   Size,
        [property: JsonPropertyName("url")]      string Url
    );

    public record VersionManifest(
        [property: JsonPropertyName("version")]   string           Version,
        [property: JsonPropertyName("changelog")] string           Changelog,
        [property: JsonPropertyName("files")]     List<UpdateFile>  Files,
        // IP y puerto del servidor — se aplican siempre al config.ini sin borrar prefs del usuario
        [property: JsonPropertyName("serverIp")]   string?          ServerIp   = null,
        [property: JsonPropertyName("serverPort")] string?          ServerPort = null
    );

    // ─────────────────────────────────────────────────────────────────────────
    //  Modelo mínimo de la GitHub Releases API
    // ─────────────────────────────────────────────────────────────────────────

    public record GitHubAsset(
        [property: JsonPropertyName("name")]                 string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl
    );

    public record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")]   List<GitHubAsset> Assets
    );

    // ─────────────────────────────────────────────────────────────────────────
    //  MainWindow
    // ─────────────────────────────────────────────────────────────────────────

    public partial class MainWindow : Window
    {
        // ── Configuración — cambia GITHUB_REPO al tuyo propio ──────────────
        private const string GITHUB_REPO    = "matthew7990/VoidboundMU";           // <-- TU REPO
        private const string GITHUB_API_URL = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";
        private const string VERSION_FILE   = "version.txt";                  // junto al Launcher.exe
        private const string MANIFEST_ASSET = "version.json";
        
        // Configuración por defecto si no existe config.ini
        private string _serverIp   = "181.97.243.64";
        private int    _serverPort = 44406; 

        private readonly string _gameRoot;
        private static readonly HttpClient _http = BuildHttpClient();
        private CancellationTokenSource _pingCts;

        public MainWindow()
        {
            InitializeComponent();
            _gameRoot = AppDomain.CurrentDomain.BaseDirectory;
            
            // Cargar IP/Puerto desde config si existe
            LoadServerConfig();

            // Iniciar procesos en background
            _pingCts = new CancellationTokenSource();
            _ = RunUpdatePipelineAsync();
            _ = RunServerStatusMonitorAsync(_pingCts.Token);
        }

        // ── Drag & ventana ──────────────────────────────────────────────────

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _pingCts.Cancel();
            Application.Current.Shutdown();
        }
        
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        // ── Pipeline principal de UPDATE ────────────────────────────────────

        private async Task RunUpdatePipelineAsync()
        {
            SetStatus("Connecting to update server...", 5);

            try
            {
                // 1. Consultar GitHub API → obtener URL del version.json
                var release = await FetchLatestReleaseAsync();
                if (release is null)
                {
                    SetStatus("Offline mode — could not reach update server.", 100);
                    EnablePlay();
                    return;
                }

                var manifestAsset = release.Assets.FirstOrDefault(a =>
                    a.Name.Equals(MANIFEST_ASSET, StringComparison.OrdinalIgnoreCase));

                if (manifestAsset is null)
                {
                    SetStatus($"Release {release.TagName} found (no manifest). Ready.", 100);
                    EnablePlay();
                    return;
                }

                SetStatus($"Checking version {release.TagName}...", 15);

                // 2. Descargar el manifiesto
                var manifest = await FetchManifestAsync(manifestAsset.BrowserDownloadUrl);
                if (manifest is null)
                {
                    SetStatus("Could not read update manifest. Offline mode.", 100);
                    EnablePlay();
                    return;
                }

                // Actualizar info de UI
                UpdateChangelog(manifest.Version, manifest.Changelog);

                // Actualizar vars de servidor para el Monitor
                if (!string.IsNullOrEmpty(manifest.ServerIp)) _serverIp = manifest.ServerIp;
                if (!string.IsNullOrEmpty(manifest.ServerPort) && int.TryParse(manifest.ServerPort, out int p)) _serverPort = p;


                // 3. Comparar versión local
                string localVersion = GetLocalVersion();
                if (localVersion == manifest.Version)
                {
                    // Aunque la versión no cambió, la IP puede haber cambiado
                    PatchConfigIni(manifest);
                    SetStatus($"Ready to play.", 100);
                    EnablePlay();
                    return;
                }

                // 4. Verificar qué archivos realmente necesitan actualización
                
                // NOTA: Si es primera vez (localVersion vacio), GetPendingFiles retornará TODOS los archivos
                // y se descargarán uno por uno.
                
                var pending = GetPendingFiles(manifest);
                if (pending.Count == 0)
                {
                    PatchConfigIni(manifest);
                    SaveLocalVersion(manifest.Version);
                    SetStatus($"Ready to play.", 100);
                    EnablePlay();
                    return;
                }

                SetStatus($"Updating {pending.Count} file(s)...", 20);

                // 5. Descargar archivos con progreso
                await DownloadFilesAsync(pending, 20, 95);

                // 6. Parchear config.ini (solo IP/puerto — preserva prefs del usuario)
                PatchConfigIni(manifest);

                // 7. Guardar versión
                SaveLocalVersion(manifest.Version);

                SetStatus($"Update complete — {manifest.Version}", 100);
            }
            catch (Exception ex)
            {
                SetStatus($"Update error — check logs.", 100);
                Debug.WriteLine($"[Launcher] Update error: {ex}");
            }

            EnablePlay();
        }

        // ── Monitor de Estado del Servidor ───────────────────────────────────

        private async Task RunServerStatusMonitorAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool isOnline = await PingServerAsync(_serverIp, _serverPort);

                Dispatcher.Invoke(() =>
                {
                    if (isOnline)
                    {
                        ServerStatusText.Text = "ONLINE";
                        ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(100, 255, 100)); // Verde
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(50, 255, 50));
                        ((DropShadowEffect)StatusDot.Effect).Color = Color.FromRgb(50, 255, 50);
                    }
                    else
                    {
                        ServerStatusText.Text = "OFFLINE";
                        ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)); // Rojo
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 50, 50));
                        ((DropShadowEffect)StatusDot.Effect).Color = Color.FromRgb(255, 50, 50);
                    }
                });

                await Task.Delay(10000, token); // Check cada 10s
            }
        }

        private async Task<bool> PingServerAsync(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                // Timeout corto (2s) para no bloquear UI
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(2000);
                
                var completed = await Task.WhenAny(connectTask, timeoutTask);
                return completed == connectTask && client.Connected;
            }
            catch 
            {
                return false;
            }
        }

        // ── GitHub API ───────────────────────────────────────────────────────

        private static async Task<GitHubRelease?> FetchLatestReleaseAsync()
        {
            try
            {
                using var response = await _http.GetAsync(GITHUB_API_URL);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<GitHubRelease>(json);
            }
            catch { return null; }
        }

        private static async Task<VersionManifest?> FetchManifestAsync(string url)
        {
            try
            {
                var json = await _http.GetStringAsync(url);
                return JsonSerializer.Deserialize<VersionManifest>(json);
            }
            catch { return null; }
        }

        // ── Lógica de verificación de archivos ───────────────────────────────

        private List<UpdateFile> GetPendingFiles(VersionManifest manifest)
        {
            var pending = new List<UpdateFile>();

            foreach (var file in manifest.Files)
            {
                string localPath = Path.Combine(_gameRoot, file.Path);

                if (!File.Exists(localPath))
                {
                    pending.Add(file);
                    continue;
                }

                // Comprobar SHA256 para no re-descargar si coincide
                string localHash = ComputeSha256(localPath);
                if (!localHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    pending.Add(file);
            }

            return pending;
        }

        private static string ComputeSha256(string filePath)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                byte[] hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch (IOException)
            {
                return ""; // Si el archivo esta en uso, forzamos re-descarga (o fallara mas adelante)
            }
        }

        // ── Descarga con barra de progreso ───────────────────────────────────

        private async Task DownloadFilesAsync(List<UpdateFile> files, double progressStart, double progressEnd)
        {
            long totalBytes = files.Sum(f => f.Size);
            long downloadedBytes = 0;
            double progressRange = progressEnd - progressStart;

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                string destPath = Path.Combine(_gameRoot, file.Path);
                string tempPath = destPath + ".tmp";

                SetStatus($"Downloading {Path.GetFileName(file.Path)}... ({i + 1}/{files.Count})",
                    progressStart + progressRange * downloadedBytes / Math.Max(totalBytes, 1));

                // Asegurar directorio destino
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                // Descargar a archivo temporal
                await DownloadFileWithProgressAsync(file.Url, tempPath, (bytesRead) =>
                {
                    // No actualizamos la barra por cada byte para no saturar UI, solo chunks
                });
                
                // Actualizar progreso general despues de cada archivo (para fluidez)
                downloadedBytes += file.Size;
                
                 // Verificar hash del archivo descargado
                string downloadedHash = ComputeSha256(tempPath);
                if (!downloadedHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                     // Retry logic simple o fail
                    File.Delete(tempPath);
                    throw new Exception($"Hash mismatch for {file.Path}. Download may be corrupted.");
                }

                // Reemplazar archivo destino
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);
            }
        }

        private async Task DownloadFileWithProgressAsync(string url, string destPath, Action<long> onProgress)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream    = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer         = new byte[81920];
            int  read;

            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                onProgress(read);
            }
        }

        // ── Configuración y Versión local ────────────────────────────────────

        private string GetLocalVersion()
        {
            string path = Path.Combine(_gameRoot, VERSION_FILE);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }

        private void SaveLocalVersion(string version)
        {
             try {
                File.WriteAllText(Path.Combine(_gameRoot, VERSION_FILE), version);
             } catch {}
        }

        private void LoadServerConfig()
        {
             // Intentar leer IP/Puerto actual del config.ini para mostrar status antes del update
             string configPath = Path.Combine(_gameRoot, @"MuVoid\config.ini");
             if (File.Exists(configPath))
             {
                 foreach(var line in File.ReadAllLines(configPath))
                 {
                     if(line.StartsWith("ServerIP=", StringComparison.OrdinalIgnoreCase))
                         _serverIp = line.Substring(9).Trim();
                     if(line.StartsWith("ServerPort=", StringComparison.OrdinalIgnoreCase))
                         int.TryParse(line.Substring(11).Trim(), out _serverPort);
                 }
             }
        }

        private void PatchConfigIni(VersionManifest manifest)
        {
            if (string.IsNullOrEmpty(manifest.ServerIp) && string.IsNullOrEmpty(manifest.ServerPort))
                return;

            string configPath = Path.Combine(_gameRoot, @"MuVoid\config.ini");

            if (!File.Exists(configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath,
                    $"[CONNECTION SETTINGS]\r\nServerIP={manifest.ServerIp ?? ""}\r\nServerPort={manifest.ServerPort ?? ""}\r\n");
                return;
            }

            var lines    = File.ReadAllLines(configPath).ToList();
            bool inConnectionSection = false;
            bool patchedIp   = false;
            bool patchedPort = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();

                if (trimmed.StartsWith("["))
                    inConnectionSection = trimmed.Equals("[CONNECTION SETTINGS]", StringComparison.OrdinalIgnoreCase);

                if (!inConnectionSection) continue;

                if (!string.IsNullOrEmpty(manifest.ServerIp) &&
                    trimmed.StartsWith("ServerIP=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i]  = $"ServerIP={manifest.ServerIp}";
                    patchedIp = true;
                }
                else if (!string.IsNullOrEmpty(manifest.ServerPort) &&
                         trimmed.StartsWith("ServerPort=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i]    = $"ServerPort={manifest.ServerPort}";
                    patchedPort = true;
                }
            }

            if (!patchedIp && !string.IsNullOrEmpty(manifest.ServerIp))
                lines.Add($"ServerIP={manifest.ServerIp}");
            if (!patchedPort && !string.IsNullOrEmpty(manifest.ServerPort))
                lines.Add($"ServerPort={manifest.ServerPort}");

            File.WriteAllLines(configPath, lines);
        }

        // ── UI helpers (thread-safe) ──────────────────────────────────────────

        private void SetStatus(string message, double percent)
        {
            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = message;
                SetProgress(percent);
            });
        }

        private void SetProgress(double percent)
        {
            Dispatcher.Invoke(() =>
            {
                percent = Math.Clamp(percent, 0, 100);
                // ProgressBarFillWidth (suponiendo ancho de 790 en XAML, ajustar segun diseño)
                // En el diseño nuevo no es fijo, asi que usamos Width relativo o GridLength.
                // En el XAML anterior usamos Width property del rectangulo.
                ProgressBarFill.Width = (ActualWidth - 60) * (percent / 100.0); // Ajuste dinámico aprox
                ProgressLabel.Text    = $"{(int)percent}%";
            });
        }

        private void EnablePlay()
        {
            Dispatcher.Invoke(() => PlayButton.IsEnabled = true);
        }

        private void UpdateChangelog(string version, string changelog)
        {
            Dispatcher.Invoke(() =>
            {
                VersionText.Text   = $"VERSION {version}";
                ChangelogText.Text = string.IsNullOrEmpty(changelog) ? "No changelog available." : changelog;
            });
        }

        // ── Lanzar el juego ──────────────────────────────────────────────────

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string[] possiblePaths =
                {
                    Path.Combine(_gameRoot, @"MuVoid\Main.exe"),
                    Path.Combine(_gameRoot, "Main.exe"),
                };

                string? exePath = possiblePaths.FirstOrDefault(File.Exists);

                if (exePath is not null)
                {
                    Process.Start(new ProcessStartInfo(exePath)
                    {
                        WorkingDirectory = Path.GetDirectoryName(exePath)
                    });
                    Application.Current.Shutdown();
                }
                else
                {
                    MessageBox.Show(
                        "Main.exe not found.\n\nMake sure the game is installed correctly.",
                        "Voidbound Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error launching game: {ex.Message}");
            }
        }

        // ── HttpClient factory ───────────────────────────────────────────────

        private static HttpClient BuildHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("MuVoidLauncher", "1.0"));
            return client;
        }
    }
}