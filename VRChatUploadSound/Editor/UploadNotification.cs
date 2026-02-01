#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace WorldUploadNotification
{
    public enum SoundSelection
    {
        Template1 = 0,
        Template2 = 1,
        Template3 = 2,
        Custom = 3
    }

    [Serializable]
    public class UploadNotificationSettings
    {
        public bool enabled = true;
        public bool toastEnabled = true;
        public SoundSelection successSelection = SoundSelection.Template1;
        public SoundSelection errorSelection = SoundSelection.Template1;
        public string customSuccessSoundPath = "";
        public string customErrorSoundPath = "";
        public float successVolume = 1.0f;
        public float errorVolume = 1.0f;

        private const string TemplateFolder = "Assets/kokoa/VRChatUploadSound/Asset";

        public string GetSuccessSoundPath()
        {
            if (successSelection == SoundSelection.Custom)
                return customSuccessSoundPath;
            return $"{TemplateFolder}/Success-{(int)successSelection + 1}.mp3";
        }

        public string GetErrorSoundPath()
        {
            if (errorSelection == SoundSelection.Custom)
                return customErrorSoundPath;
            return $"{TemplateFolder}/Fail-{(int)errorSelection + 1}.mp3";
        }

        private static string SettingsPath => Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "ProjectSettings",
            "UploadNotificationSettings.json");

        private static UploadNotificationSettings _instance;
        public static UploadNotificationSettings Instance
        {
            get
            {
                if (_instance == null) Load();
                return _instance;
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _instance = JsonUtility.FromJson<UploadNotificationSettings>(json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UploadNotification] 設定の読み込みに失敗: {ex.Message}");
            }
            _instance ??= new UploadNotificationSettings();
        }

        public static void Save()
        {
            try
            {
                var json = JsonUtility.ToJson(_instance, true);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UploadNotification] 設定の保存に失敗: {ex.Message}");
            }
        }
    }

    [InitializeOnLoad]
    public static class WorldUploadNotificationSound
    {
        private static bool _isRegistered = false;
        private static bool _worldHooksRegistered = false;
        private static bool _avatarHooksRegistered = false;

        // キャッシュ
        private static Type _controlPanelType;
        private static Type _worldApiType;
        private static Type _avatarApiType;
        private static object _worldBuilder;
        private static object _avatarBuilder;
        private static Delegate _worldSuccessHandler;
        private static Delegate _worldErrorHandler;
        private static Delegate _worldBuildErrorHandler;
        private static Delegate _avatarSuccessHandler;
        private static Delegate _avatarErrorHandler;
        private static Delegate _avatarBuildErrorHandler;

        // ログ監視による重複通知防止
        private static double _lastBuildErrorTime = 0;
        private const double BUILD_ERROR_COOLDOWN = 10.0; // 10秒間は重複通知しない
        private static bool _buildErrorNotified = false;

        static WorldUploadNotificationSound()
        {
            // ログメッセージを監視してビルドエラーを即座に検知
            Application.logMessageReceived += OnLogMessageReceived;

            // VRCSdkControlPanelをリフレクションで取得（グローバル名前空間）
            _controlPanelType = FindType("VRCSdkControlPanel");
            if (_controlPanelType == null)
            {
                Debug.LogWarning("[UploadNotification] VRCSdkControlPanel not found");
                return;
            }

            // SDK Panelのイベントを購読
            var enableEvent = _controlPanelType.GetEvent("OnSdkPanelEnable");
            var disableEvent = _controlPanelType.GetEvent("OnSdkPanelDisable");

            if (enableEvent != null)
            {
                var handler = Delegate.CreateDelegate(
                    enableEvent.EventHandlerType,
                    typeof(WorldUploadNotificationSound).GetMethod(nameof(OnSdkPanelEnable),
                        BindingFlags.NonPublic | BindingFlags.Static));
                enableEvent.AddEventHandler(null, handler);
            }

            if (disableEvent != null)
            {
                var handler = Delegate.CreateDelegate(
                    disableEvent.EventHandlerType,
                    typeof(WorldUploadNotificationSound).GetMethod(nameof(OnSdkPanelDisable),
                        BindingFlags.NonPublic | BindingFlags.Static));
                disableEvent.AddEventHandler(null, handler);
            }
        }

        private static Type FindType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null) return type;
            }
            return null;
        }

        private static void OnSdkPanelEnable(object sender, EventArgs e)
        {
            RegisterUploadHooks();
        }

        private static void OnSdkPanelDisable(object sender, EventArgs e)
        {
            UnregisterUploadHooks();
        }

        private static void RegisterUploadHooks()
        {
            if (_isRegistered) return;

            // World Builder
            if (TryRegisterWorldHooks())
            {
                _worldHooksRegistered = true;
            }

            // Avatar Builder
            if (TryRegisterAvatarHooks())
            {
                _avatarHooksRegistered = true;
            }

            _isRegistered = _worldHooksRegistered || _avatarHooksRegistered;
        }

        private static bool TryRegisterWorldHooks()
        {
            try
            {
                _worldApiType = FindType("VRC.SDK3.Editor.IVRCSdkWorldBuilderApi");
                if (_worldApiType == null) return false;

                _worldBuilder = TryGetBuilder(_worldApiType);
                if (_worldBuilder == null) return false;

                // インターフェースではなく実際の型からイベントを取得
                var builderType = _worldBuilder.GetType();
                var successEvent = builderType.GetEvent("OnSdkUploadSuccess");
                if (successEvent != null)
                {
                    _worldSuccessHandler = Delegate.CreateDelegate(
                        successEvent.EventHandlerType,
                        typeof(WorldUploadNotificationSound).GetMethod(nameof(OnWorldUploadSuccess),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    successEvent.AddEventHandler(_worldBuilder, _worldSuccessHandler);
                }

                var errorEvent = builderType.GetEvent("OnSdkUploadError");
                if (errorEvent != null)
                {
                    _worldErrorHandler = Delegate.CreateDelegate(
                        errorEvent.EventHandlerType,
                        typeof(WorldUploadNotificationSound).GetMethod(nameof(OnUploadError),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    errorEvent.AddEventHandler(_worldBuilder, _worldErrorHandler);
                }

                // ビルドエラーもフック
                var buildErrorEvent = builderType.GetEvent("OnSdkBuildError");
                if (buildErrorEvent != null)
                {
                    _worldBuildErrorHandler = Delegate.CreateDelegate(
                        buildErrorEvent.EventHandlerType,
                        typeof(WorldUploadNotificationSound).GetMethod(nameof(OnBuildError),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    buildErrorEvent.AddEventHandler(_worldBuilder, _worldBuildErrorHandler);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.Log($"[UploadNotification] World SDK not available: {ex.Message}");
            }
            return false;
        }

        private static bool TryRegisterAvatarHooks()
        {
            try
            {
                _avatarApiType = FindType("VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi");
                if (_avatarApiType == null) return false;

                _avatarBuilder = TryGetBuilder(_avatarApiType);
                if (_avatarBuilder == null) return false;

                // インターフェースではなく実際の型からイベントを取得
                var builderType = _avatarBuilder.GetType();
                var successEvent = builderType.GetEvent("OnSdkUploadSuccess");
                if (successEvent != null)
                {
                    _avatarSuccessHandler = Delegate.CreateDelegate(
                        successEvent.EventHandlerType,
                        typeof(WorldUploadNotificationSound).GetMethod(nameof(OnAvatarUploadSuccess),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    successEvent.AddEventHandler(_avatarBuilder, _avatarSuccessHandler);
                }

                var errorEvent = builderType.GetEvent("OnSdkUploadError");
                if (errorEvent != null)
                {
                    _avatarErrorHandler = Delegate.CreateDelegate(
                        errorEvent.EventHandlerType,
                        typeof(WorldUploadNotificationSound).GetMethod(nameof(OnUploadError),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    errorEvent.AddEventHandler(_avatarBuilder, _avatarErrorHandler);
                }

                // ビルドエラーもフック
                var buildErrorEvent = builderType.GetEvent("OnSdkBuildError");
                if (buildErrorEvent != null)
                {
                    _avatarBuildErrorHandler = Delegate.CreateDelegate(
                        buildErrorEvent.EventHandlerType,
                        typeof(WorldUploadNotificationSound).GetMethod(nameof(OnBuildError),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    buildErrorEvent.AddEventHandler(_avatarBuilder, _avatarBuildErrorHandler);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UploadNotification] Avatar SDK registration failed: {ex}");
            }
            return false;
        }

        private static object TryGetBuilder(Type apiType)
        {
            if (_controlPanelType == null || apiType == null) return null;

            var tryGetBuilderMethod = _controlPanelType.GetMethod("TryGetBuilder");
            if (tryGetBuilderMethod == null) return null;

            var genericMethod = tryGetBuilderMethod.MakeGenericMethod(apiType);
            var parameters = new object[] { null };
            var result = (bool)genericMethod.Invoke(null, parameters);

            return result ? parameters[0] : null;
        }

        private static void UnregisterUploadHooks()
        {
            if (!_isRegistered) return;

            try
            {
                if (_worldHooksRegistered && _worldBuilder != null)
                {
                    var builderType = _worldBuilder.GetType();

                    var successEvent = builderType.GetEvent("OnSdkUploadSuccess");
                    successEvent?.RemoveEventHandler(_worldBuilder, _worldSuccessHandler);

                    var errorEvent = builderType.GetEvent("OnSdkUploadError");
                    errorEvent?.RemoveEventHandler(_worldBuilder, _worldErrorHandler);

                    var buildErrorEvent = builderType.GetEvent("OnSdkBuildError");
                    buildErrorEvent?.RemoveEventHandler(_worldBuilder, _worldBuildErrorHandler);
                }
            }
            catch (Exception) { /* クリーンアップ時の例外は無視 */ }

            try
            {
                if (_avatarHooksRegistered && _avatarBuilder != null)
                {
                    var builderType = _avatarBuilder.GetType();

                    var successEvent = builderType.GetEvent("OnSdkUploadSuccess");
                    successEvent?.RemoveEventHandler(_avatarBuilder, _avatarSuccessHandler);

                    var errorEvent = builderType.GetEvent("OnSdkUploadError");
                    errorEvent?.RemoveEventHandler(_avatarBuilder, _avatarErrorHandler);

                    var buildErrorEvent = builderType.GetEvent("OnSdkBuildError");
                    buildErrorEvent?.RemoveEventHandler(_avatarBuilder, _avatarBuildErrorHandler);
                }
            }
            catch (Exception) { /* クリーンアップ時の例外は無視 */ }

            _isRegistered = false;
            _worldHooksRegistered = false;
            _avatarHooksRegistered = false;
            _worldBuilder = null;
            _avatarBuilder = null;
        }

        private static void OnWorldUploadSuccess(object sender, string worldId)
        {
            if (!UploadNotificationSettings.Instance.enabled) return;
            Debug.Log($"[UploadNotification] ワールドのアップロードが完了しました！ ID: {worldId}");
            PlayNotificationSound(true);
            ShowNotification("ワールドアップロード完了！", true);
        }

        private static void OnAvatarUploadSuccess(object sender, string avatarId)
        {
            if (!UploadNotificationSettings.Instance.enabled) return;
            Debug.Log($"[UploadNotification] アバターのアップロードが完了しました！ ID: {avatarId}");
            PlayNotificationSound(true);
            ShowNotification("アバターアップロード完了！", true);
        }

        private static void OnUploadError(object sender, string error)
        {
            if (!UploadNotificationSettings.Instance.enabled) return;
            Debug.LogError($"[UploadNotification] アップロードエラー: {error}");
            PlayNotificationSound(false);
            ShowNotification("アップロード失敗...", false);
        }

        private static void OnBuildError(object sender, string error)
        {
            // ログ監視で既に通知済みの場合はスキップ
            if (_buildErrorNotified)
            {
                _buildErrorNotified = false; // 次回のためにリセット
                return;
            }

            if (!UploadNotificationSettings.Instance.enabled) return;
            Debug.Log($"[UploadNotification] ビルドエラー（イベント）: {error}");
            PlayNotificationSound(false);
            ShowNotification("ビルド失敗...", false);
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            // エラーログのみ対象
            if (type != LogType.Error) return;
            if (!UploadNotificationSettings.Instance.enabled) return;

            // 自分自身のログは無視
            if (condition.Contains("[UploadNotification]")) return;

            // ビルドエラーを検知
            bool isBuildError = condition.Contains("Failed to build") ||
                                condition.Contains("VRCSDK build was aborted") ||
                                condition.Contains("Failed to assign network IDs");

            if (isBuildError)
            {
                // クールダウン中はスキップ
                if (EditorApplication.timeSinceStartup - _lastBuildErrorTime < BUILD_ERROR_COOLDOWN)
                {
                    return;
                }

                _lastBuildErrorTime = EditorApplication.timeSinceStartup;
                _buildErrorNotified = true; // イベントハンドラでの重複通知を防止

                #if UNITY_EDITOR_WIN
                // パスを事前に取得（メインスレッドで）
                var settings = UploadNotificationSettings.Instance;
                string soundPath = settings.GetErrorSoundPath();
                float volume = settings.errorVolume;
                string dataPath = Application.dataPath;
                bool toastEnabled = settings.toastEnabled;

                // ダイアログ表示中でも即座に通知するため、別スレッドで実行
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        PlaySoundDirect(soundPath, volume, dataPath);
                        if (toastEnabled)
                        {
                            ShowWindowsToastDirect("VRChat SDK", "ビルド失敗...");
                        }
                    }
                    catch (Exception) { /* バックグラウンドスレッドの例外は無視 */ }
                });
                #endif
            }
        }

        #if UNITY_EDITOR_WIN
        private static void PlaySoundDirect(string soundPath, float volume, string dataPath)
        {
            if (!string.IsNullOrEmpty(soundPath))
            {
                string fullPath;
                if (soundPath.StartsWith("Assets/"))
                {
                    fullPath = Path.Combine(Path.GetDirectoryName(dataPath), soundPath);
                }
                else
                {
                    fullPath = soundPath;
                }

                fullPath = fullPath.Replace("/", "\\");

                if (File.Exists(fullPath))
                {
                    // PowerShellでMediaPlayerを使って再生（ダイアログ表示中でも動作）
                    string script = $@"
Add-Type -AssemblyName PresentationCore
$player = New-Object System.Windows.Media.MediaPlayer
$player.Volume = {volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}
$player.Open([Uri]'{fullPath.Replace("'", "''")}')
Start-Sleep -Milliseconds 500
$player.Play()
while ($player.Position -lt $player.NaturalDuration.TimeSpan -and $player.NaturalDuration.HasTimeSpan) {{ Start-Sleep -Milliseconds 100 }}
Start-Sleep -Milliseconds 500
Remove-Item $MyInvocation.MyCommand.Path -Force
";
                    string tempScript = Path.Combine(Path.GetTempPath(), $"upload_notification_sound_{System.Threading.Thread.CurrentThread.ManagedThreadId}.ps1");
                    File.WriteAllText(tempScript, script, System.Text.Encoding.UTF8);

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(startInfo);
                    return;
                }
            }

            System.Media.SystemSounds.Hand.Play();
        }
        #endif

        private static void PlayNotificationSound(bool isSuccess = true)
        {
            var settings = UploadNotificationSettings.Instance;
            string soundPath = isSuccess ? settings.GetSuccessSoundPath() : settings.GetErrorSoundPath();
            float volume = isSuccess ? settings.successVolume : settings.errorVolume;

            if (!string.IsNullOrEmpty(soundPath))
            {
                // プロジェクト内ファイル (Assets/...)
                if (soundPath.StartsWith("Assets/"))
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(soundPath);
                    if (clip != null)
                    {
                        PlayClip(clip, volume);
                        return;
                    }
                }
                // 外部ファイル (絶対パス) - AudioUtilで再生
                else if (File.Exists(soundPath))
                {
                    PlayExternalSound(soundPath, volume);
                    return;
                }
            }

            // フォールバック: デフォルトの通知音
            #if UNITY_EDITOR_WIN
            PlayDefaultNotificationSound(volume, isSuccess);
            #else
            Debug.LogWarning("[UploadNotification] サウンドファイルが設定されていません");
            #endif
        }

        #if UNITY_EDITOR_WIN
        private static void PlayDefaultNotificationSound(float volume, bool isSuccess)
        {
            try
            {
                // Windowsの通知音のパスを取得
                string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                string[] successSounds = new[]
                {
                    Path.Combine(windowsDir, "Media", "Windows Notify System Generic.wav"),
                    Path.Combine(windowsDir, "Media", "Windows Notify Calendar.wav"),
                    Path.Combine(windowsDir, "Media", "chimes.wav")
                };

                string[] errorSounds = new[]
                {
                    Path.Combine(windowsDir, "Media", "Windows Notify Email.wav"),
                    Path.Combine(windowsDir, "Media", "Windows Critical Stop.wav"),
                    Path.Combine(windowsDir, "Media", "chord.wav")
                };

                var sounds = isSuccess ? successSounds : errorSounds;
                string soundFile = null;
                foreach (var path in sounds)
                {
                    if (File.Exists(path))
                    {
                        soundFile = path;
                        break;
                    }
                }

                if (soundFile != null)
                {
                    PlayExternalSound(soundFile, volume);
                }
                else
                {
                    // 最終フォールバック
                    if (isSuccess)
                        System.Media.SystemSounds.Asterisk.Play();
                    else
                        System.Media.SystemSounds.Hand.Play();

                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UploadNotification] デフォルト音再生エラー: {ex.Message}");
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
        #endif

        private static void PlayExternalSound(string path, float volume)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLower();
                AudioType audioType = ext switch
                {
                    ".wav" => AudioType.WAV,
                    ".mp3" => AudioType.MPEG,
                    ".ogg" => AudioType.OGGVORBIS,
                    ".aiff" or ".aif" => AudioType.AIFF,
                    _ => AudioType.UNKNOWN
                };

                if (audioType == AudioType.UNKNOWN)
                {
                    Debug.LogWarning($"[UploadNotification] 未対応の形式: {ext}");
                    return;
                }

                var uri = new Uri(path).AbsoluteUri;
                var request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
                var operation = request.SendWebRequest();

                // 同期的に待機 (Editor専用なので問題なし)
                while (!operation.isDone) { }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        PlayClip(clip, volume);
                    }
                }
                else
                {
                    Debug.LogWarning($"[UploadNotification] 音声読み込み失敗: {request.error}");
                    #if UNITY_EDITOR_WIN
                    System.Media.SystemSounds.Asterisk.Play();
                    #endif
                }

                request.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UploadNotification] 音声再生に失敗: {ex.Message}");
                #if UNITY_EDITOR_WIN
                System.Media.SystemSounds.Asterisk.Play();
                #endif
            }
        }

        private static GameObject _audioSourceObject;
        private static AudioSource _audioSource;
        private static float _clipEndTime;

        private static void PlayClip(AudioClip clip, float volume)
        {
            PlayClipWithAudioSource(clip, volume);
        }

        private static void PlayClipWithAudioSource(AudioClip clip, float volume)
        {
            // 既存のAudioSourceをクリーンアップ
            CleanupAudioSource();

            // 新しいAudioSourceを作成
            _audioSourceObject = new GameObject("UploadNotificationAudioSource");
            _audioSourceObject.hideFlags = HideFlags.HideAndDontSave;
            _audioSource = _audioSourceObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.volume = Mathf.Clamp01(volume);
            _audioSource.clip = clip;
            _audioSource.Play();

            // 再生終了時間を記録
            _clipEndTime = (float)EditorApplication.timeSinceStartup + clip.length + 0.1f;

            // 再生終了を監視
            EditorApplication.update += CheckClipFinished;


        }

        private static void CheckClipFinished()
        {
            if (EditorApplication.timeSinceStartup >= _clipEndTime ||
                _audioSource == null || !_audioSource.isPlaying)
            {
                EditorApplication.update -= CheckClipFinished;
                CleanupAudioSource();
            }
        }

        private static void CleanupAudioSource()
        {
            if (_audioSourceObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_audioSourceObject);
                _audioSourceObject = null;
                _audioSource = null;
            }
        }

        private static void ShowNotification(string message, bool isSuccess = true)
        {
            // Windowsトースト通知
            #if UNITY_EDITOR_WIN
            if (UploadNotificationSettings.Instance.toastEnabled)
            {
                ShowWindowsToast("VRChat SDK", message, isSuccess);
            }
            #endif
        }

        #if UNITY_EDITOR_WIN
        private static void ShowWindowsToast(string title, string message, bool isSuccess)
        {
            try
            {
                // 一時ファイルにPowerShellスクリプトを書き出して実行
                string tempScript = Path.Combine(Path.GetTempPath(), "upload_notification_toast.ps1");

                string script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$template = @'
<toast>
    <visual>
        <binding template=""ToastText02"">
            <text id=""1"">{EscapeXml(title)}</text>
            <text id=""2"">{EscapeXml(message)}</text>
        </binding>
    </visual>
    </toast>
'@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Unity Editor').Show($toast)
Remove-Item $MyInvocation.MyCommand.Path -Force
";

                File.WriteAllText(tempScript, script, System.Text.Encoding.UTF8);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                System.Diagnostics.Process.Start(startInfo);
                // 非同期で実行（待機しない）
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UploadNotification] トースト通知エラー: {ex.Message}");
            }
        }

        private static string EscapeXml(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        // 別スレッドから呼び出し可能なトースト通知（ログ出力なし）
        private static void ShowWindowsToastDirect(string title, string message)
        {
            try
            {
                string tempScript = Path.Combine(Path.GetTempPath(), $"upload_notification_toast_{System.Threading.Thread.CurrentThread.ManagedThreadId}.ps1");

                string script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$template = @'
<toast>
    <visual>
        <binding template=""ToastText02"">
            <text id=""1"">{EscapeXml(title)}</text>
            <text id=""2"">{EscapeXml(message)}</text>
        </binding>
    </visual>
    </toast>
'@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Unity Editor').Show($toast)
Remove-Item $MyInvocation.MyCommand.Path -Force
";

                File.WriteAllText(tempScript, script, System.Text.Encoding.UTF8);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                System.Diagnostics.Process.Start(startInfo);
            }
            catch (Exception) { /* 別スレッドなのでログ出力しない */ }
        }
        #endif

        [MenuItem("Tools/Upload Notification Sound")]
        private static void OpenSettings()
        {
            UploadNotificationSettingsWindow.ShowWindow();
        }

        public static void TestSound()
        {
            Debug.Log("[UploadNotification] 成功音テスト再生...");
            PlayNotificationSound(true);
            ShowNotification("アップロード成功テスト！", true);
        }

        public static void TestErrorSound()
        {
            Debug.Log("[UploadNotification] 失敗音テスト再生...");
            PlayNotificationSound(false);
            ShowNotification("アップロード失敗テスト！", false);
        }

        public static bool IsWorldSdkAvailable() => FindType("VRC.SDK3.Editor.IVRCSdkWorldBuilderApi") != null;
        public static bool IsAvatarSdkAvailable() => FindType("VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi") != null;
    }

    public class UploadNotificationSettingsWindow : EditorWindow
    {
        private const string VERSION = "1.0.0";
        private static readonly string[] SuccessSoundLabels = { "電子レンジのチン", "電子音１", "電子音２", "カスタム" };
        private static readonly string[] ErrorSoundLabels = { "電子音１", "ﾎﾟﾖﾖｰﾝ", "トランペット", "カスタム" };

        private static GUIStyle _headerStyle;
        private static GUIStyle _boxStyle;
        private static GUIStyle _versionStyle;
        private Vector2 _scrollPosition;

        public static void ShowWindow()
        {
            var window = GetWindow<UploadNotificationSettingsWindow>("Upload Notification");
            window.minSize = new Vector2(350, 420);
            window.Show();
        }

        private void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter,
                    margin = new RectOffset(0, 0, 10, 5)
                };
            }

            if (_versionStyle == null)
            {
                _versionStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.gray }
                };
            }

            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle("HelpBox")
                {
                    padding = new RectOffset(10, 10, 10, 10),
                    margin = new RectOffset(5, 5, 5, 5)
                };
            }
        }

        private void OnGUI()
        {
            InitStyles();
            var settings = UploadNotificationSettings.Instance;

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // ヘッダー
            EditorGUILayout.Space(5);
            GUILayout.Label("Upload Notification", _headerStyle);
            GUILayout.Label($"v{VERSION} by kokoa", _versionStyle);

            // SDK状態（コンパクト表示）
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            DrawSdkStatusCompact("World", WorldUploadNotificationSound.IsWorldSdkAvailable());
            GUILayout.Space(10);
            DrawSdkStatusCompact("Avatar", WorldUploadNotificationSound.IsAvatarSdkAvailable());
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            EditorGUI.BeginChangeCheck();

            // 基本設定セクション
            EditorGUILayout.BeginVertical(_boxStyle);
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            settings.enabled = EditorGUILayout.Toggle("通知を有効にする", settings.enabled);
            #if UNITY_EDITOR_WIN
            settings.toastEnabled = EditorGUILayout.Toggle("Windowsトースト通知", settings.toastEnabled);
            #endif
            EditorGUILayout.EndVertical();

            // 通知が無効の場合はグレーアウト
            EditorGUI.BeginDisabledGroup(!settings.enabled);

            // 成功音セクション
            EditorGUILayout.BeginVertical(_boxStyle);
            DrawSoundSelector("成功音", SuccessSoundLabels, ref settings.successSelection, 
                ref settings.customSuccessSoundPath, ref settings.successVolume, true);
            EditorGUILayout.EndVertical();

            // 失敗音セクション
            EditorGUILayout.BeginVertical(_boxStyle);
            DrawSoundSelector("失敗音", ErrorSoundLabels, ref settings.errorSelection, 
                ref settings.customErrorSoundPath, ref settings.errorVolume, false);
            EditorGUILayout.EndVertical();

            EditorGUI.EndDisabledGroup();

            if (EditorGUI.EndChangeCheck())
            {
                UploadNotificationSettings.Save();
            }

            EditorGUILayout.Space(10);

            // テストボタン
            EditorGUI.BeginDisabledGroup(!settings.enabled);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            
            var testButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 30
            };
            
            if (GUILayout.Button("▶ 成功音テスト", testButtonStyle, GUILayout.Width(120)))
            {
                WorldUploadNotificationSound.TestSound();
            }
            GUILayout.Space(10);
            if (GUILayout.Button("▶ 失敗音テスト", testButtonStyle, GUILayout.Width(120)))
            {
                WorldUploadNotificationSound.TestErrorSound();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);
            EditorGUILayout.EndScrollView();
        }

        private void DrawSdkStatusCompact(string name, bool available)
        {
            var color = available ? new Color(0.3f, 0.8f, 0.3f) : Color.gray;
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = color },
                fontStyle = FontStyle.Bold
            };
            GUILayout.Label($"[{(available ? "OK" : "--")}] {name} SDK", style);
        }

        private void DrawSoundSelector(string label, string[] soundLabels, ref SoundSelection selection, 
            ref string customPath, ref float volume, bool isSuccess)
        {
            // ヘッダーと試聴ボタン
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("▶", GUILayout.Width(25), GUILayout.Height(18)))
            {
                if (isSuccess)
                    WorldUploadNotificationSound.TestSound();
                else
                    WorldUploadNotificationSound.TestErrorSound();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            // ドロップダウンで選択
            selection = (SoundSelection)EditorGUILayout.Popup("サウンド", (int)selection, soundLabels);

            // カスタム選択時のみファイル選択UI表示
            if (selection == SoundSelection.Custom)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(true);
                if (string.IsNullOrEmpty(customPath))
                {
                    EditorGUILayout.TextField("（未設定）");
                }
                else if (customPath.StartsWith("Assets/"))
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(customPath);
                    EditorGUILayout.ObjectField(clip, typeof(AudioClip), false);
                }
                else
                {
                    EditorGUILayout.TextField(Path.GetFileName(customPath));
                }
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("選択", GUILayout.Width(50)))
                {
                    var path = SelectSoundFile(customPath);
                    if (!string.IsNullOrEmpty(path))
                    {
                        customPath = path;
                        UploadNotificationSettings.Save();
                    }
                }

                if (GUILayout.Button("×", GUILayout.Width(25)))
                {
                    customPath = "";
                    UploadNotificationSettings.Save();
                }
                EditorGUILayout.EndHorizontal();
            }

            // 音量スライダー
            EditorGUILayout.BeginHorizontal();
            volume = EditorGUILayout.Slider("音量", volume, 0f, 1f);
            EditorGUILayout.LabelField($"{Mathf.RoundToInt(volume * 100)}%", GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
        }

        private string SelectSoundFile(string currentPath)
        {
            var projectPath = Application.dataPath;
            string startPath;

            if (string.IsNullOrEmpty(currentPath))
            {
                startPath = projectPath;
            }
            else if (currentPath.StartsWith("Assets/"))
            {
                startPath = Path.GetDirectoryName(Path.Combine(projectPath, "..", currentPath));
            }
            else
            {
                startPath = Path.GetDirectoryName(currentPath);
            }

            var path = EditorUtility.OpenFilePanel(
                "サウンドを選択",
                startPath ?? projectPath,
                "wav,mp3,ogg,aiff");

            if (string.IsNullOrEmpty(path)) return null;

            // プロジェクト内ならAssets相対パス、外部なら絶対パス
            if (path.StartsWith(projectPath))
            {
                return "Assets" + path.Substring(projectPath.Length).Replace("\\", "/");
            }
            return path.Replace("\\", "/");
        }
    }
}
#endif
