using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

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
        // ───────────────────────────────────────────────────────────────────

        private readonly string _gameRoot;
        private static readonly HttpClient _http = BuildHttpClient();

        public MainWindow()
        {
            InitializeComponent();
            _gameRoot = AppDomain.CurrentDomain.BaseDirectory;
            _ = RunUpdatePipelineAsync();
        }

        // ── Drag & ventana ──────────────────────────────────────────────────

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        // ── Pipeline principal ───────────────────────────────────────────────

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

                // 3. Comparar versión local
                string localVersion = GetLocalVersion();
                if (localVersion == manifest.Version)
                {
                    // Aunque la versión no cambió, la IP puede haber cambiado
                    PatchConfigIni(manifest);
                    SetStatus($"Already up to date — {manifest.Version}", 100);
                    EnablePlay();
                    return;
                }

                // 4. Verificar qué archivos realmente necesitan actualización
                var pending = GetPendingFiles(manifest);
                if (pending.Count == 0)
                {
                    PatchConfigIni(manifest);
                    SaveLocalVersion(manifest.Version);
                    SetStatus($"All files verified — {manifest.Version}", 100);
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
                if (!string.IsNullOrEmpty(manifest.Changelog))
                    UpdateChangelog(manifest.Version, manifest.Changelog);
            }
            catch (Exception ex)
            {
                SetStatus($"Update error — offline mode enabled.", 100);
                Debug.WriteLine($"[Launcher] Update error: {ex}");
            }

            EnablePlay();
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
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
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
                    double pct = progressStart + progressRange *
                        Math.Min((downloadedBytes + bytesRead) / (double)Math.Max(totalBytes, 1), 1.0);
                    SetProgress(pct);
                });

                // Verificar hash del archivo descargado
                string downloadedHash = ComputeSha256(tempPath);
                if (!downloadedHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new Exception($"Hash mismatch for {file.Path}. Download may be corrupted.");
                }

                // Reemplazar archivo destino
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);

                downloadedBytes += file.Size;
            }
        }

        private async Task DownloadFileWithProgressAsync(string url, string destPath, Action<long> onProgress)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream    = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer         = new byte[81920];
            long bytesRead     = 0;
            int  read;

            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                bytesRead += read;
                onProgress(bytesRead);
            }
        }

        // ── Versión local ────────────────────────────────────────────────────

        private string GetLocalVersion()
        {
            string path = Path.Combine(_gameRoot, VERSION_FILE);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }

        private void SaveLocalVersion(string version)
        {
            File.WriteAllText(Path.Combine(_gameRoot, VERSION_FILE), version);
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
                ProgressBarFill.Width = 740 * (percent / 100.0);
                ProgressLabel.Text    = $"{(int)percent}%";
            });
        }

        private void EnablePlay()
        {
            Dispatcher.Invoke(() => PlayButton.IsEnabled = true);
        }

        // ── Parchear config.ini ──────────────────────────────────────────────

        private void PatchConfigIni(VersionManifest manifest)
        {
            if (string.IsNullOrEmpty(manifest.ServerIp) && string.IsNullOrEmpty(manifest.ServerPort))
                return;

            // Busca el config.ini dentro de MuVoid/ (produccion)
            string configPath = Path.Combine(_gameRoot, @"MuVoid\config.ini");

            // Si no existe todavia, lo crea desde cero con los valores del release
            if (!File.Exists(configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath,
                    $"[CONNECTION SETTINGS]\r\nServerIP={manifest.ServerIp ?? ""}\r\nServerPort={manifest.ServerPort ?? ""}\r\n");
                Debug.WriteLine($"[Launcher] config.ini creado con IP={manifest.ServerIp}");
                return;
            }

            // Lee las lineas existentes y parchea solo ServerIP / ServerPort
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

            // Si la seccion no tenia las claves, las agrega al final de [CONNECTION SETTINGS]
            if (!patchedIp && !string.IsNullOrEmpty(manifest.ServerIp))
                lines.Add($"ServerIP={manifest.ServerIp}");
            if (!patchedPort && !string.IsNullOrEmpty(manifest.ServerPort))
                lines.Add($"ServerPort={manifest.ServerPort}");

            File.WriteAllLines(configPath, lines);
            Debug.WriteLine($"[Launcher] config.ini parcheado — IP={manifest.ServerIp} Port={manifest.ServerPort}");
        }

        private void UpdateChangelog(string version, string changelog)
        {
            Dispatcher.Invoke(() =>
            {
                VersionText.Text   = $"VERSION {version}";
                ChangelogText.Text = changelog;
            });
        }

        // ── Lanzar el juego ──────────────────────────────────────────────────

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Busca Main.exe en MuVoid/ (producción) o en las rutas de desarrollo
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
                        "Main.exe not found.\n\nExpected path: MuVoid\\Main.exe\n\nMake sure the MuVoid folder is next to MuVoidLauncher.exe",
                        "MuVoid Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            // GitHub API requiere User-Agent
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("MuVoidLauncher", "1.0"));
            return client;
        }
    }
}