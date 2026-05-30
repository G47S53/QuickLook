// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

extern alias MediaInfoWrapper;
using MediaInfoWrapper::MediaInfo;
using QuickLook.Common.Annotations;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.VideoViewer.AudioTrack;
using QuickLook.Plugin.VideoViewer.LyricTrack;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UtfUnknown;
using WPFMediaKit.DirectShow.Controls;
using WPFMediaKit.DirectShow.MediaPlayers;
using Newtonsoft.Json;  // GUILLAUME: Ajout de Newtonsoft.Json pour la sérialisation/désérialisation des paramètres personnalisés de l'utilisateur dans un fichier JSON.

namespace QuickLook.Plugin.VideoViewer;

public partial class ViewerPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly ContextObject _context;
    private BitmapSource _coverArt;
    private DispatcherTimer _lyricTimer;
    private LrcLine[] _lyricLines;
    private MidiPlayer _midiPlayer;

    private bool _hasVideo;
    private bool _isPlaying;
    private bool _wasPlaying;
    private bool _shouldLoop;
    private bool _useHardwareAcceleration;

    // Guillaume: Ajout d'une configuration, avec persistance dans un fichier JSON, pour la molette de défilement et l'auto-fermeture à la fin de la vidéo.
    private readonly string configPath = Path.Combine(
                                         Environment.GetFolderPath(
                                         Environment.SpecialFolder.ApplicationData),
                                         "QuickLook",
                                         "VideoViewerSettings.json");
    
    // Guillaume: Ajout d'un objet de settings pour stocker les configurations personnalisées de l'utilisateur, avec sérialisation/désérialisation JSON.
    private VideoViewerSettings settings;

    // Guillaume: Ajout d'une propriété pour activer/désactiver l'auto-fermeture à la fin de la vidéo, avec mise à jour de l'interface utilisateur en conséquence.
    public bool AutoCloseEnabled;

    public ViewerPanel(ContextObject context)
    {
        InitializeComponent();
        LoadAndInsertGlassLayer();

        // apply global theme
        Resources.MergedDictionaries[0].MergedDictionaries.Clear();

        _context = context;

        mediaElement.MediaUriPlayer.LAVFilterDirectory =
            IntPtr.Size == 8 ? @"LAVFilters-x64\" : @"LAVFilters-x86\";

        //ShowViedoControlContainer(null, null);
        viewerPanel.PreviewMouseMove += ShowViedoControlContainer;

        mediaElement.MediaUriPlayer.PlayerStateChanged += PlayerStateChanged;
        mediaElement.MediaOpened += MediaOpened;
        mediaElement.MediaEnded += MediaEnded;
        mediaElement.MediaFailed += MediaFailed;

        ShouldLoop = SettingHelper.Get("ShouldLoop", false, "QuickLook.Plugin.VideoViewer");
        UseHardwareAcceleration = SettingHelper.Get("UseHardwareAcceleration", false, "QuickLook.Plugin.VideoViewer");

        // Apply persisted HW/SW mode to the underlying player if supported.
        HardwareAccelerationModeChanged(UseHardwareAcceleration);

        string translationFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Translations.config");
        buttonPlayPause.ToolTip = TranslationHelper.Get("BTN_PlayPause", translationFile, failsafe: "Play/Pause");
        buttonLoop.ToolTip = TranslationHelper.Get("BTN_Loop", translationFile, failsafe: "Loop");
        buttonHardwareAcceleration.ToolTip = TranslationHelper.Get("BTN_HardwareAcceleration", translationFile, failsafe: "Hardware/Software Decoding");
        buttonMute.ToolTip = TranslationHelper.Get("BTN_Volume", translationFile, failsafe: "Volume");
        buttonTime.ToolTip = TranslationHelper.Get("BTN_Time", translationFile, failsafe: "Time Elapsed/Remaining");

        buttonPlayPause.Click += TogglePlayPause;
        buttonLoop.Click += ToggleShouldLoop;
        buttonHardwareAcceleration.Click += ToggleHardwareAcceleration;
        buttonTime.Click += (_, _) => buttonTime.Tag = (string)buttonTime.Tag == "Time" ? "Length" : "Time";
        buttonMute.Click += (_, _) => volumeSliderLayer.Visibility = Visibility.Visible;
        volumeSliderLayer.MouseDown += (_, _) => volumeSliderLayer.Visibility = Visibility.Collapsed;

        // Guillaume: Chargement des paramètres personnalisés de l'utilisateur depuis un fichier JSON, avec gestion des exceptions pour éviter les plantages en cas de problème de lecture ou de format.
        LoadSettings();

        // Guillaume: Attribution de la commande de changement de mode de seek à la fois au clic sur le bouton dédié de l'interface et à la molette de la souris, avec mise à jour de l'interface utilisateur et persistance du choix dans les paramètres.
        buttonSeekMode.Click += ButtonSeekMode_Click;

        // Guillaume: Attribution de la commande d'activation/désactivation de l'auto-fermeture à la fin de la vidéo au clic sur le bouton dédié de l'interface, avec mise à jour de l'interface utilisateur et persistance du choix dans les paramètres.
        buttonAutoClose.Click += (_, _) =>
        {
            AutoCloseEnabled = !AutoCloseEnabled;

            UpdateAutoCloseUI();

            settings.CloseWhenFinished = AutoCloseEnabled;
            SaveSettings();
        };

        // Guillaume: Ajout de la gestion du clic gauche sur la vidéo pour basculer entre lecture et pause, avec affichage d'une info-bulle indiquant le temps actuel et la durée totale de la vidéo lors de la pause.
        mediaElement.MouseLeftButtonDown += (_, e) =>
        {
            if (mediaElement.IsPlaying)
            {
                mediaElement.Pause();
                ShowVideoInfo($"Pause : {(new TimeSpan(mediaElement.MediaPosition)):mm\\:ss} / {(new TimeSpan(mediaElement.MediaDuration)):mm\\:ss}");
            }
            else
            {
                mediaElement.Play();
            }

            e.Handled = true;
        };

        // Guillaume: Ajout de la gestion du clic et du glissement sur la barre de progression pour permettre à l'utilisateur de naviguer dans la vidéo, avec mise en pause pendant le glissement et reprise de la lecture à la fin du glissement si la vidéo était en cours de lecture.
        sliderProgress.PreviewMouseDown += (_, e) =>
        {
            _wasPlaying = mediaElement.IsPlaying;
            mediaElement.Pause();
        };

        // Les 2 commandes suivantes étaient déjà presente avant mes modifications.
        sliderProgress.PreviewMouseUp += (_, _) =>
        {
            if (_wasPlaying) mediaElement.Play();
        };
        sliderProgress.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(sliderProgress);

            double ratio = pos.X / sliderProgress.ActualWidth;

            ratio = Math.Max(0, Math.Min(1, ratio));

            long newPos = (long)(sliderProgress.Maximum * ratio);

            sliderProgress.Value = newPos;

            mediaElement.MediaPosition = newPos;

            e.Handled = true;
        };

        // Guillaume: Lignes ci-dessous commentée pour éviter les conflits avec la commande de changement de mode de seek attribuée à la molette de la souris.
        //PreviewMouseWheel += (_, e) => ChangeVolume(e.Delta / 120d * 0.04d);

        // Guillaume: Ajout de la gestion de la molette de la souris pour permettre à l'utilisateur de naviguer dans la vidéo en fonction du mode de seek sélectionné (5% de la durée totale, 1 seconde ou 5 secondes), avec mise à jour de l'interface utilisateur pour afficher le temps actuel et la durée totale lors du changement de position, et fermeture automatique de la fenêtre si l'option est activée et que la fin de la vidéo est atteinte.
        PreviewMouseWheel += (_, e) =>
        {
            if (mediaElement == null)
                return;

            long step = 0;

            switch (seekMode)
            {
                case 0:
                    step = (long)(mediaElement.MediaDuration * 0.05);
                    break;

                case 1:
                    step = TimeSpan.FromSeconds(1).Ticks;
                    break;

                case 2:
                    step = TimeSpan.FromSeconds(5).Ticks;
                    break;
            }

            long newPos = mediaElement.MediaPosition + (e.Delta > 0 ? step : -step);

            if (newPos < 0)
                newPos = 0;
            if (newPos > mediaElement.MediaDuration)
            {
                newPos = mediaElement.MediaDuration;
                mediaElement.MediaPosition = newPos;
                ShowVideoInfo($"{(new TimeSpan(newPos)):mm\\:ss} / " + $"{(new TimeSpan(mediaElement.MediaDuration)):mm\\:ss}");
                mediaElement.Pause();
                if (AutoCloseEnabled)
                    Window.GetWindow(this)?.Close();
                return;
            }

            mediaElement.MediaPosition = newPos;

            ShowVideoInfo($"{(new TimeSpan(newPos)):mm\\:ss} / " + $"{(new TimeSpan(mediaElement.MediaDuration)):mm\\:ss}");

            e.Handled = true;
        };
    }

    // Guillaume: Ajout d'une méthode pour charger les paramètres personnalisés de l'utilisateur depuis un fichier JSON, avec gestion des exceptions pour éviter les plantages en cas de problème de lecture ou de format, et mise à jour de l'interface utilisateur en conséquence.
    private void LoadSettings()
    {
        try
        {
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);

                settings = JsonConvert.DeserializeObject<VideoViewerSettings>(json);

                seekMode = settings.SeekMode;
                AutoCloseEnabled = settings.CloseWhenFinished;

                UpdateSeekModeUISilencieux();
                UpdateAutoCloseUI();
            }
            else
            {
                settings = new VideoViewerSettings();
            }
        }
        catch
        {
            settings = new VideoViewerSettings();
        }
    }

    // Guillaume: Ajout d'une méthode pour sauvegarder les paramètres personnalisés de l'utilisateur dans un fichier JSON, avec gestion des exceptions pour éviter les plantages en cas de problème d'écriture.
    private void SaveSettings()
    {
        try
        {
            string folder =
                Path.GetDirectoryName(configPath);

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);

            File.WriteAllText(configPath, json);
        }
        catch
        {
        }
    }

    // Guillaume: Ajout d'un champ pour stocker le mode de seek sélectionné par l'utilisateur (0 pour 5% de la durée totale, 1 pour 1 seconde, 2 pour 5 secondes).
    private int seekMode = 0;

    // Guillaume: Ajout d'une méthode pour gérer le clic sur le bouton de changement de mode de seek.
    private void ButtonSeekMode_Click(object sender, RoutedEventArgs e)
    {
        CycleSeekMode();
    }

    // Guillaume: Ajout d'une méthode pour faire défiler les modes de seek disponibles (5% de la durée totale, 1 seconde, 5 secondes) à chaque appel.
    private void CycleSeekMode()
    {
        seekMode++;

        if (seekMode > 2)
            seekMode = 0;

        UpdateSeekModeUI();

        settings.SeekMode = seekMode;

        SaveSettings();
    }

    // Guillaume: Ajout d'une méthode pour mettre à jour l'interface utilisateur en fonction de l'état de l'option d'auto-fermeture à la fin de la vidéo, en modifiant l'opacité du bouton dédié et en ajoutant une décoration de texte barré au label associé lorsque l'option est désactivée.
    private void UpdateAutoCloseUI()
    {
        buttonAutoClose.Opacity = AutoCloseEnabled ? 1d : 0.5d;
        textAutoClose.TextDecorations = AutoCloseEnabled ? null : TextDecorations.Strikethrough;
    }

    // Guillaume: Ajout d'une méthode pour mettre à jour l'interface utilisateur en fonction du mode de seek sélectionné.
    private void UpdateSeekModeUI()
    {
        switch (seekMode)
        {
            case 0:
                textSeekMode.Text = ":  5%";
                ShowVideoInfo("Molette : 5%");
                break;
            case 1:
                textSeekMode.Text = ":  1s";
                ShowVideoInfo("Molette : 1s");
                break;
            case 2:
                textSeekMode.Text = ":  5s";
                ShowVideoInfo("Molette : 5s");
                break;
        }
    }

    // Guillaume: Ajout d'une méthode pour mettre à jour silencieusement l'interface utilisateur en fonction du mode de seek sélectionné, sans afficher d'info-bulle.
    // Utile pour le chargement initial des paramètres sans afficher d'info-bulle inutile.
    private void UpdateSeekModeUISilencieux()
    {
        switch (seekMode)
        {
            case 0:
                textSeekMode.Text = ":  5%";
                break;
            case 1:
                textSeekMode.Text = ":  1s";
                break;
            case 2:
                textSeekMode.Text = ":  5s";
                break;
        }
    }

    // Guillaume: Ajout d'une méthode pour afficher une info-bulle au centre de la vidéo avec un texte personnalisé.
    private void ShowVideoInfo(string text)
    {
        videoInfoText.Text = text;

        var storyboard =
            (Storyboard)videoInfoPopup.Resources["StoryboardShowVideoInfo"];

        storyboard.Begin();
    }

    // Guillaume: Ajout d'une classe pour stocker les paramètres personnalisés de l'utilisateur.
    public class VideoViewerSettings
    {
        public bool CloseWhenFinished { get; set; } = false;

        public int SeekMode { get; set; } = 2;
    }

    private partial void LoadAndInsertGlassLayer();

    public bool HasVideo
    {
        get => _hasVideo;
        private set
        {
            if (value == _hasVideo) return;
            _hasVideo = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (value == _isPlaying) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public bool ShouldLoop
    {
        get => _shouldLoop;
        private set
        {
            if (value == _shouldLoop) return;
            _shouldLoop = value;
            OnPropertyChanged();
        }
    }

    public bool UseHardwareAcceleration
    {
        get => _useHardwareAcceleration;
        private set
        {
            if (value == _useHardwareAcceleration) return;
            _useHardwareAcceleration = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource CoverArt
    {
        get => _coverArt;
        private set
        {
            if (ReferenceEquals(value, _coverArt)) return;
            if (value == null) return;
            _coverArt = value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        SettingHelper.Set("VolumeDouble", LinearVolume, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("ShouldLoop", ShouldLoop, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");

        try
        {
            mediaElement?.Close();

            Task.Run(() =>
            {
                mediaElement?.MediaUriPlayer.Dispose();
                mediaElement = null;
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;
        _midiPlayer?.Dispose();
        _midiPlayer = null;
    }

    private void Panel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var wnd = Window.GetWindow(this);
            // Do not allow dragging when window is borderless (e.g. fullscreen)
            if (wnd?.WindowStyle == WindowStyle.None)
                return;

            wnd?.DragMove();
        }

        // GUILLAUME: Utilisation de la molette de la souris pour changer le mode de seek (5%, 1s, 5s)
        // La commande est annulée pour éviter les conflits avec la  commande générale de fermeture de l'application

        /*if (e.ChangedButton == MouseButton.Middle)
        {
            CycleSeekMode();
        }*/
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void MediaOpened(object o, RoutedEventArgs args)
    {
        if (mediaElement == null)
            return;

        HasVideo = mediaElement.HasVideo;

        _context.IsBusy = false;
    }

    private void MediaFailed(object sender, MediaFailedEventArgs e)
    {
        ((MediaUriElement)sender).Dispatcher.BeginInvoke(new Action(() =>
        {
            _context.ViewerContent = new TextBlock()
            {
                Text = e.Exception.ToString(),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _context.IsBusy = false;
        }));
    }

    private void MediaEnded(object sender, RoutedEventArgs e)
    {
        if (mediaElement == null)
            return;

        mediaElement.MediaPosition = 0L;
        if (ShouldLoop)
        {
            IsPlaying = true;

            mediaElement.Play();
        }
        else
        {
            IsPlaying = false;

            mediaElement.Pause();

            // GUILLAUME: Ferme la fenêtre si l'option Autoclose est activée et que la vidéo est arrivée à sa fin.
            if (AutoCloseEnabled)
                Window.GetWindow(this)?.Close();
        }
    }

    private void ShowViedoControlContainer(object sender, MouseEventArgs e)
    {
        var show = (Storyboard)videoControlContainer.FindResource("ShowControlStoryboard");
        if (videoControlContainer.Opacity == 0d || videoControlContainer.Opacity == 1d)
            show.Begin();
    }

    private void AutoHideViedoControlContainer(object sender, EventArgs e)
    {
        if (!HasVideo)
            return;

        if (videoControlContainer.IsMouseOver)
            return;

        var hide = (Storyboard)videoControlContainer.FindResource("HideControlStoryboard");

        hide.Begin();
    }

    private void PlayerStateChanged(PlayerState oldState, PlayerState newState)
    {
        switch (newState)
        {
            case PlayerState.Playing:
                IsPlaying = true;
                break;

            case PlayerState.Paused:
            case PlayerState.Stopped:
            case PlayerState.Closed:
                IsPlaying = false;
                break;
        }
    }

    private void UpdateMeta(string path, MediaInfoLib info)
    {
        if (HasVideo)
            return;

        try
        {
            if (info == null)
                throw new NullReferenceException();

            var title = info.Get(StreamKind.General, 0, "Title");
            var artist = info.Get(StreamKind.General, 0, "Performer");
            var album = info.Get(StreamKind.General, 0, "Album");

            metaTitle.Text = !string.IsNullOrWhiteSpace(title) ? title : Path.GetFileName(path);
            metaArtists.Text = artist;
            metaAlbum.Text = album;

            // Extract cover art
            var coverData = info.Get(StreamKind.General, 0, "Cover_Data");
            var coverBytes = CoverDataExtractor.Extract(coverData);
            CoverArt = CoverDataExtractor.Extract(coverBytes);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            metaTitle.Text = Path.GetFileName(path);
            metaArtists.Text = metaAlbum.Text = string.Empty;
        }

        metaArtists.Visibility = string.IsNullOrEmpty(metaArtists.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        metaAlbum.Visibility = string.IsNullOrEmpty(metaAlbum.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var lyricPath = Path.ChangeExtension(path, ".lrc");

        // Stop previous timer if any.
        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;

        if (File.Exists(lyricPath))
        {
            var buffer = File.ReadAllBytes(lyricPath);
            var encoding = CharsetDetector.DetectFromBytes(buffer).Detected?.Encoding ?? Encoding.Default;

            _lyricLines = [.. LrcHelper.ParseText(encoding.GetString(buffer))];
        }
        else
        {
            // Use embedded lyrics from MediaInfo if present.
            // Common tag: General/Lyrics (may contain LRC formatted content).
            var embeddedLyrics = info?.Get(StreamKind.General, 0, "Lyrics");

            // Only check whether the tag of lyrics is present by MediaInfo
            if (!string.IsNullOrWhiteSpace(embeddedLyrics))
            {
                var file = TagLib.File.Create(path);
                embeddedLyrics = file.Tag.Lyrics;

                // Check whether the tag of lyrics is present by TagLib#
                if (!string.IsNullOrWhiteSpace(embeddedLyrics))
                {
                    _lyricLines = [.. LrcHelper.ParseText(embeddedLyrics)];
                }
            }
        }

        if (_lyricLines != null && _lyricLines.Length != 0)
        {
            _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _lyricTimer.Tick += (sender, e) =>
            {
                if (_lyricLines != null && _lyricLines.Length != 0)
                {
                    var lyric = LrcHelper.GetNearestLrc(_lyricLines, new TimeSpan(mediaElement.MediaPosition));
                    metaLyric.Text = lyric?.LrcText?.Trim();
                }
                else
                {
                    metaLyric.Text = null;
                    metaLyric.Visibility = Visibility.Collapsed;
                }
            };
            _lyricTimer.Start();

            metaLyric.Visibility = Visibility.Visible;
        }
        else
        {
            metaLyric.Visibility = Visibility.Collapsed;
        }
    }

    public double LinearVolume
    {
        get => mediaElement.Volume;
        set
        {
            mediaElement.Volume = value;
            OnPropertyChanged();
        }
    }

    private void ChangeVolume(double delta)
    {
        LinearVolume = Math.Max(0d, Math.Min(1d, LinearVolume + delta));
    }

    private void TogglePlayPause(object sender, EventArgs e)
    {
        if (mediaElement.IsPlaying)
            mediaElement.Pause();
        else
            mediaElement.Play();
    }

    private void ToggleShouldLoop(object sender, EventArgs e)
    {
        ShouldLoop = !ShouldLoop;
    }

    private void ToggleHardwareAcceleration(object sender, EventArgs e)
    {
        UseHardwareAcceleration = !UseHardwareAcceleration;
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");
        HardwareAccelerationModeChanged(UseHardwareAcceleration);
    }

    private void HardwareAccelerationModeChanged(bool enable)
    {
        try
        {
            var player = mediaElement?.MediaUriPlayer;
            if (player == null) return;

            if (mediaElement.Source == null)
            {
                // No source loaded yet – just store the flag for the next Open

                // GUILLAUME: Lignes ci-dessous commentée pour éviter une exception de cross-threading, car le player n'est pas encore initialisé et ne peut pas recevoir d'invocation.
                
                /*player.Dispatcher.BeginInvoke(() =>
                //    player.EnableLAVHardwareAcceleration = enable);*/

                return;
            }

            // Dispatch to the player's own MTA thread.
            // ApplyHardwareAcceleration will call OpenSource() there, which
            // rebuilds the full graph (incl. EVR/VMR9 allocator) so that
            // NewAllocatorSurface fires and the WPF back buffer is refreshed.
            // Position + play state are restored inside ApplyHardwareAcceleration
            // via a MediaOpened callback.
            player.Dispatcher.BeginInvoke(() =>
                player.ApplyHardwareAcceleration(enable));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void LoadAndPlay(string path, MediaInfoLib info)
    {
        // Detect whether it is other playback formats
        if (!HasVideo)
        {
            string audioCodec = info?.Get(StreamKind.Audio, 0, "Format");

            if (audioCodec?.Equals("MIDI", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                _midiPlayer = new MidiPlayer(this, _context);
                _midiPlayer.LoadAndPlay(path);
                return; // Midi player will handle the playback at all
            }
        }

        UpdateMeta(path, info);

        // detect rotation
        _ = double.TryParse(info?.Get(StreamKind.Video, 0, "Rotation"), out var rotation);
        // Correct rotation: on some machine the value "90" becomes "90000" by some reason
        if (rotation > 360d)
            rotation /= 1e3;
        if (Math.Abs(rotation) > 0.1d)
            mediaElement.LayoutTransform = new RotateTransform(rotation, 0.5d, 0.5d);

        mediaElement.Source = new Uri(path);
        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        LinearVolume = Math.Max(0d, Math.Min(1d, SettingHelper.Get("VolumeDouble", 1d, "QuickLook.Plugin.VideoViewer")));

        mediaElement.Play();
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
