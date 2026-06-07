// Copyright © 2024 QL-Win Contributors
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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TDxInput;
using Media3D = System.Windows.Media.Media3D;

namespace QuickLook.Plugin.ImageViewer.Pano360
{
    public partial class Pano360Panel : UserControl, IDisposable
    {
        #region Constantes et Déclarations de champs

        // ─────────────────────────────────────────────────────────────────────
        // > Constantes — paramètres de la sphère et de la navigation
        // ─────────────────────────────────────────────────────────────────────
        private const int SphereSlices = 72;
        private const int SphereStacks = 36;

        private const double FovMin = 30.0;
        private const double FovMax = 120.0;
        private const double FovDefault = 90.0;
        private const double FovZoomStep = 5.0;

        private const double MouseSensitivity = 1.0;
        private const double MouseDeadZone = 5.0;

        private const double VerticalAngleMin = -90.0;
        private const double VerticalAngleMax = 45.0;

        // ─────────────────────────────────────────────────────────────────────
        // > Autorotation
        // ─────────────────────────────────────────────────────────────────────
        // Durée d'un tour complet (360°) en secondes.
        // Changez ces valeurs pour accélérer ou ralentir chaque mode.
        private const double AutoRotateFastSeconds = 10.0;   // Tour rapide   : 10 s
        private const double AutoRotateNormalSeconds = 20.0; // Tour normal   : 20 s
        private const double AutoRotateSlowSeconds = 60.0;   // Tour lent     : 60 s

        // ─────────────────────────────────────────────────────────────────────
        // > Opacité progressive de la barre de boutons
        // ─────────────────────────────────────────────────────────────────────
        // Délai d'inactivité (en secondes) avant de réafficher les boutons.
        private const double InactivityDelay = 0.5;

        // ─────────────────────────────────────────────────────────────────────
        // > Champs 3D
        // ─────────────────────────────────────────────────────────────────────
        private PerspectiveCamera _camera = new PerspectiveCamera();
        private AxisAngleRotation3D _horizontalRotation = new AxisAngleRotation3D();
        private AxisAngleRotation3D _verticalRotation = new AxisAngleRotation3D();

        // ─────────────────────────────────────────────────────────────────────
        // > Champs navigation souris
        // ─────────────────────────────────────────────────────────────────────
        private bool _isMouseDown = false;
        private double _startMouseX;
        private double _startMouseY;
        private double _lastMouseX;
        private double _lastMouseY;
        private TimeSpan _lastRenderTime;
        private bool _isMouseInertia = false; // Indique si le mouvement résiduel vient d'un lancer de souris
        private bool _isPopupShown = false; // Évite les déclenchements multiples du popup avant la fermeture

        // ─────────────────────────────────────────────────────────────────────
        // > Champs SpaceMouse 3Dconnexion
        // ─────────────────────────────────────────────────────────────────────
        private Device _smDevice;
        private Sensor _smSensor;

        private bool _spaceMouseEnabled = false;

        private readonly object _spaceMouseLock = new object();
        private double _rawSpaceMouseX = 0;
        private double _rawSpaceMouseY = 0;
        private double _rawSpaceMouseZ = 0;

        // ─────────────────────────────────────────────────────────────────────
        // > Autorotation
        // ─────────────────────────────────────────────────────────────────────
        private enum AutoRotationState { Off, Lent, Normal, Rapide }

        // État courant de l'autorotation (commence arrêté).
        private AutoRotationState _autoRotState = AutoRotationState.Off;

        // ── Variables pour l'autorotation haute précision ───────────────────
        //private System.Diagnostics.Stopwatch _autoRotateStopwatch = new();
        //private double _angleAuDemarrage = 0;

        // ─────────────────────────────────────────────────────────────────────
        // > AutoClose - Moteur Physique "La Jamais Contente" 🏎️ (Accélération / Décélération)
        // ─────────────────────────────────────────────────────────────────────
        private double _currentRotationSpeed = 0.0;   // Vitesse angulaire actuelle (°/s)
        private const double AccelerationRate = 18.0; // Taux d'accélération (°/s²)
        private const double DecelerationRate = 45.0; // Taux de freinage/décélération (°/s²)

        private bool _autoCloseActive = false;
        private double _autoCloseTargetAngle = -1;
        private double _autoCloseStartAngle = -1;
        private bool _hasLeftStartZone = false;       // Sécurité pour éviter la fermeture instantanée au clic
        private bool _isAutoClosingPhase = false;     // True quand le tour est fini et qu'on freine avant fermeture

        // ─────────────────────────────────────────────────────────────────────
        // > Opacité progressive
        // ─────────────────────────────────────────────────────────────────────
        // Horodatage du dernier mouvement détecté (souris OU autorotation OU SpaceMouse).
        private DateTime _lastMovementTime = DateTime.MinValue;

        // Indique si un mouvement était actif au frame précédent.
        // Sert à détecter le passage repos ↔ mouvement sans heuristique trop lourde.

        private bool _isBarreMasquee = false;
        private bool _isMouseOverBarre = false;

        // ─────────────────────────────────────────────────────────────────────
        // > Référence au contexte QuickLook et au chemin du fichier image
        // ─────────────────────────────────────────────────────────────────────
        private readonly QuickLook.Common.Plugin.ContextObject _context;
        private readonly string _imagePath;

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        // Constructeur
        // ─────────────────────────────────────────────────────────────────────
        public Pano360Panel(QuickLook.Common.Plugin.ContextObject context, string imagePath)
        {
            // ── Forcer la priorité haute pour éliminer les micro-saccades Windows ──
            try
            {
                using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                // On passe en priorité haute pour garantir la synchronisation thread UI / Render
                currentProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            }
            catch
            {
                // Sécurité au cas où Windows refuserait le changement de priorité
            }

            InitializeComponent();
            _context = context;
            _imagePath = imagePath;

            // Initialisation de la scène 3D dès que le contrôle est chargé
            Loaded += (s, e) => InitScene();
            Unloaded += (s, e) => Dispose();

            // Clic droit → bascule plein écran
            MouseRightButtonUp += (s, e) => ToggleFullscreen();

            //Active la spacemouse par défaut
            btnSpaceMouse.IsChecked = true;

            // AutoClose désactivé par défaut au démarrage
            MajEtatAutoClose();
        }

        #region Initilisation de la scène 3D

        // ─────────────────────────────────────────────────────────────────────
        // Initialisation de la scène
        // ─────────────────────────────────────────────────────────────────────
        private void InitScene()
        {
            SetupCamera();
            SetupLight();
            var material = CreatePanoramaMaterial(_imagePath);
            SetupSphere(material);
            SetupEventHandlers();

            txtLoading.Visibility = Visibility.Collapsed;
            _context.IsBusy = false;  // Signale à QuickLook que le chargement est terminé

            Focus();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mise en place de la caméra
        // ─────────────────────────────────────────────────────────────────────
        private void SetupCamera()
        {
            _camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0, 0),
                LookDirection = new Media3D.Vector3D(0, 0, 1),
                UpDirection = new Media3D.Vector3D(0, 1, 0),
                FieldOfView = FovDefault
            };
            viewport3D.Camera = _camera;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lumière ambiante (éclaire uniformément toute la sphère)
        // ─────────────────────────────────────────────────────────────────────
        private void SetupLight()
        {
            viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new AmbientLight(Colors.White)
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction de la sphère et application du matériau (texture 360°)
        // ─────────────────────────────────────────────────────────────────────
        private void SetupSphere(Material material)
        {
            var mesh = CreatePanoramaSphere();
            var model = new GeometryModel3D(mesh, material)
            {
                BackMaterial = material
            };

            // Deux rotations indépendantes : horizontale (axe Y) et verticale (axe X).
            _horizontalRotation = new AxisAngleRotation3D(new Media3D.Vector3D(0, 1, 0), 0);
            _verticalRotation = new AxisAngleRotation3D(new Media3D.Vector3D(1, 0, 0), 0);

            var transformGroup = new Transform3DGroup();
            transformGroup.Children.Add(new RotateTransform3D(_horizontalRotation));
            transformGroup.Children.Add(new RotateTransform3D(_verticalRotation));
            model.Transform = transformGroup;

            viewport3D.Children.Add(new ModelVisual3D { Content = model });
        }

        #endregion Fin de l'initialisation de la scène 3D

        // ─────────────────────────────────────────────────────────────────────
        // Gestionnaires d'événements
        // ─────────────────────────────────────────────────────────────────────
        private void SetupEventHandlers()
        {
            // CompositionTarget.Rendering est appelé à chaque frame WPF rendu.
            // C'est ici que l'animation de rotation et le fondu de la barre sont gérés.
            CompositionTarget.Rendering += OnRendering;

            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
            MouseMove += OnMouseMove;
            MouseWheel += OnMouseWheel;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Gestion de la souris
        // ─────────────────────────────────────────────────────────────────────
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_context != null) _context.BlocageShowCaption = true;

            if (e.MiddleButton == MouseButtonState.Pressed) Window.GetWindow(this)?.Close();

            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (_autoRotState != AutoRotationState.Off)
            {
                _autoRotState = AutoRotationState.Off;
                btnAutoRotate.Content = "⏸ Off";
                MajEtatAutoClose();
            }

            _isMouseDown = true;
            var pos = e.GetPosition(this);
            _lastMouseX = _startMouseX = pos.X;
            _lastMouseY = _startMouseY = pos.Y;
            Mouse.Capture(this);

            MarquerMouvement();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            //if (_context != null) _context.BlocageShowCaption = false;

            _isMouseDown = false;
            Mouse.Capture(null);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMouseDown) return;

            var pos = e.GetPosition(this);
            _lastMouseX = pos.X;
            _lastMouseY = pos.Y;

            // La souris bouge alors qu'elle est pressée → mouvement en cours
            MarquerMouvement();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0 ? -FovZoomStep : FovZoomStep;
            double newFov = Clamp(_camera.FieldOfView + delta, FovMin, FovMax);
            _camera.FieldOfView = newFov;
            txtFov.Text = string.Format("(FOV: {0:F0}°)", newFov);

            // On lance l'animation XAML
            if (txtFov.Resources["FadeFovStoryboard"] is Storyboard sb)
            {
                // On applique le storyboard spécifiquement sur le txtFov
                sb.Begin(txtFov);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Gestion de la SpaceMouse 3Dconnexion
        // ─────────────────────────────────────────────────────────────────────
        private void BtnSpaceMouse_Checked(object sender, RoutedEventArgs e)
            => ConnecterSpaceMouse();

        private void BtnSpaceMouse_Unchecked(object sender, RoutedEventArgs e)
            => DeconnecterSpaceMouse();

        private void ConnecterSpaceMouse()
        {
            if (_spaceMouseEnabled) return;

            try
            {
                _smDevice = new Device();
                _smSensor = _smDevice.Sensor;
                _smDevice.Connect();

                // On s'abonne à l'événement natif de la V1
                _smSensor.SensorInput += OnSpaceMouseMouvement;
                _spaceMouseEnabled = true;
                System.Diagnostics.Debug.WriteLine("[Pano Panel] SpaceMouse connectée en direct !");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Pano Panel] Échec connexion SpaceMouse : " + ex.Message);
            }
        }

        private void DeconnecterSpaceMouse()
        {
            if (!_spaceMouseEnabled) return;

            try
            {
                if (_smSensor != null)
                    _smSensor.SensorInput -= OnSpaceMouseMouvement;

                if (_smDevice != null && _smDevice.IsConnected)
                    _smDevice.Disconnect();
            }
            catch { }
            finally
            {
                _smDevice = null;
                _smSensor = null;
                _spaceMouseEnabled = false;
            }
        }

        private void OnSpaceMouseMouvement()
        {
            if (_smSensor == null) return;

            try
            {
                lock (_spaceMouseLock)
                {
                    double axeX = _smSensor.Rotation.X;
                    double axeY = _smSensor.Rotation.Y;
                    double axeZ = _smSensor.Rotation.Z;
                    double intensite = _smSensor.Rotation.Angle;

                    // Debug complet avec les 3 axes pour y voir clair
                    System.Diagnostics.Debug.WriteLine($"SpaceMouse -> X:{axeX:F0} | Y:{axeY:F0} | Z:{axeZ:F0} | Angle:{intensite:F0}");

                    _rawSpaceMouseX = axeX * intensite;
                    _rawSpaceMouseY = axeY * intensite;
                    _rawSpaceMouseZ = axeZ * intensite;
                }
            }
            catch
            {
                // Sécurité COM
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // BOUTON AUTOROTATION — cycle entre les 4 états
        // ─────────────────────────────────────────────────────────────────────
        /// Gestionnaire du clic sur le bouton "AutoRotate".
        /// À chaque clic, l'état avance dans le cycle :
        ///   Off  →  Lent  →  Normal →  Rapide  →  Off  → …
        /// Le libellé du bouton est mis à jour pour refléter l'état courant.
        private void BtnAutoRotate_Click(object sender, RoutedEventArgs e)
        {
            switch (_autoRotState)
            {
                case AutoRotationState.Off:
                    _autoRotState = AutoRotationState.Lent;
                    btnAutoRotate.Content = "🐢 Lent";
                    break;

                case AutoRotationState.Lent:
                    _autoRotState = AutoRotationState.Normal;
                    btnAutoRotate.Content = "▶️ Normal";
                    break;

                case AutoRotationState.Normal:
                    _autoRotState = AutoRotationState.Rapide;
                    btnAutoRotate.Content = "▶️▶️ Rapide";
                    break;

                case AutoRotationState.Rapide:
                    _autoRotState = AutoRotationState.Off;
                    btnAutoRotate.Content = "⏸ Off";
                    _lastMovementTime = DateTime.Now;
                    break;
            }
            MajEtatAutoClose();
        }
        // ─────────────────────────────────────────────────────────────────────
        // BOUTON AUTOCLOSE 
        // ─────────────────────────────────────────────────────────────────────
        private void BtnAutoClose_Click(object sender, RoutedEventArgs e)
        {
            // Sécurité : ne fait rien si l'autorotation est coupée
            if (_autoRotState == AutoRotationState.Off) return;

            _autoCloseActive = !_autoCloseActive;

            if (_autoCloseActive)
            {
                // On mémorise l'angle actuel exact de la rotation horizontale
                _autoCloseStartAngle = _horizontalRotation.Angle;
                _autoCloseTargetAngle = _autoCloseStartAngle;
                _hasLeftStartZone = false; // On vient juste d'arriver sur l'angle
                _isPopupShown = false; // 🔄 Réinitialisation
            }
            else
            {
                _autoCloseTargetAngle = -1;
                _autoCloseStartAngle = -1;
                _hasLeftStartZone = false;
            }

            MajEtatAutoClose();
        }
        private void MajEtatAutoClose()
        {
            if (_autoRotState == AutoRotationState.Off)
            {
                _autoCloseActive = false;
                _isAutoClosingPhase = false;
                _autoCloseTargetAngle = -1;
                _autoCloseStartAngle = -1;
                _hasLeftStartZone = false;

                btnAutoClose.IsEnabled = false;
                txtAutoClose.Text = "🚪 AutoClose: Off";
                txtAutoClose.TextDecorations = TextDecorations.Strikethrough;
                txtAutoClose.Opacity = 0.5;

                panelAutoCloseProgress.Visibility = Visibility.Collapsed; // 🛑 Masqué si rotation Off
            }
            else
            {
                // Le bouton devient accessible car l'autorotation tourne
                btnAutoClose.IsEnabled = true;
                txtAutoClose.Opacity = 1.0;

                if (_autoCloseActive)
                {
                    txtAutoClose.Text = "🚪 Close après 1 tour";
                    txtAutoClose.TextDecorations = null;                      // Pas barré
                    panelAutoCloseProgress.Visibility = Visibility.Visible;   // 🟢 Visible si AutoClose Actif
                }
                else
                {
                    txtAutoClose.Text = "🚪 AutoClose: Off";
                    txtAutoClose.TextDecorations = null;                      // Pas barré
                    panelAutoCloseProgress.Visibility = Visibility.Collapsed; // 🛑 Masqué si désactivé
                }
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // Barre de BOUTONS - Gestion de l'opacité
        // ─────────────────────────────────────────────────────────────────────
        private void BarreBtn_MouseEnter(object sender, MouseEventArgs e)
        {
            _isMouseOverBarre = true;
            UpdateBarreOpacity(); // Force la réapparition immédiate
        }

        private void BarreBtn_MouseLeave(object sender, MouseEventArgs e)
        {
            // On remet une "bûche" dans le compteur pour donner un petit sursis avant que ça ne re-disparaisse
            _isMouseOverBarre = false;
        }
        private void UpdateBarreOpacity()
        {
            // Si la souris est physiquement au-dessus de la barre, on la force visible
            if (_isMouseOverBarre)
            {
                if (_isBarreMasquee)
                {
                    (barreBtn.Resources["FadeInBarreBtn"] as Storyboard)?.Begin(barreBtn);
                    (panelAutoCloseProgress.Resources["FadeInProgress"] as Storyboard)?.Begin(panelAutoCloseProgress);
                    _isBarreMasquee = false;
                }
                return;
            }

            // Détermination si le panorama est considéré "en mouvement"
            // 1. Soit la souris est enfoncée (drag)
            // 2. Soit l'autorotation est active
            // 3. Soit le dernier mouvement enregistré est plus récent que le délai d'inactivité
            bool isActuellementEnMouvement = _isMouseDown ||
                                             (_autoRotState != AutoRotationState.Off) ||
                                             (DateTime.Now - _lastMovementTime).TotalSeconds < InactivityDelay;

            if (isActuellementEnMouvement)
            {
                // Le panorama bouge : on applique le fondu transparent (FadeOut)
                if (!_isBarreMasquee)
                {
                    (barreBtn.Resources["FadeOutBarreBtn"] as Storyboard)?.Begin(barreBtn);
                    (panelAutoCloseProgress.Resources["FadeOutProgress"] as Storyboard)?.Begin(panelAutoCloseProgress);
                    _isBarreMasquee = true;
                }
            }
            else
            {
                // Le panorama est à l'arrêt complet depuis un moment : on réaffiche (FadeIn)
                if (_isBarreMasquee)
                {
                    (barreBtn.Resources["FadeInBarreBtn"] as Storyboard)?.Begin(barreBtn);
                    (panelAutoCloseProgress.Resources["FadeInProgress"] as Storyboard)?.Begin(panelAutoCloseProgress);
                    _isBarreMasquee = false;
                }
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────────────
        // BOUCLE DE RENDU — appelée à chaque frame WPF
        // ─────────────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────────────
        private void OnRendering(object sender, EventArgs e)
        {
            var args = (RenderingEventArgs)e;
            double elapsed = (args.RenderingTime - _lastRenderTime).TotalSeconds;

            _lastRenderTime = args.RenderingTime;

            // --- LE TEST : Si l'écran est trop rapide (ex: 144Hz), on ignore la frame 
            // pour forcer un rythme de 60 FPS maximum (1 frame toutes les ~16ms) ---
            if (elapsed < 0.016) return;

            // Protection contre les délais aberrants (ex. : fenêtre minimisée)
            if (elapsed > 0.1) return;

            // ──────────────────────────────────────────────────────────────────────
            // ── 1. NAVIGATION SOURIS (Capture de la vitesse pour l'inertie) ───────
            if (_isMouseDown)
            {
                double deltaX = _lastMouseX - _startMouseX;
                double deltaY = _lastMouseY - _startMouseY;

                // Mode Souris : La vitesse horizontale devient proportionnelle à l'écart de drag.
                _currentRotationSpeed = Math.Abs(deltaX) < MouseDeadZone ? 0 : deltaX * MouseSensitivity;

                // 🎯 On active le drapeau : la vitesse courante appartient à la souris
                _isMouseInertia = true;

                // La rotation verticale reste en direct (pas d'inertie verticale)
                double speedY = Math.Abs(deltaY) < MouseDeadZone ? 0 : deltaY * MouseSensitivity * elapsed;
                ClampVertical(_verticalRotation.Angle - speedY);
            }

            // ──────────────────────────────────────────────────────────────────────
            // ── 2. NAVIGATION SPACEMOUSE (Directe & Instinctive) ──────────────────
            double spaceMouseSpeedX = 0;
            double spaceMouseSpeedY = 0;
            double spaceMouseSpeedZ = 0;

            lock (_spaceMouseLock)
            {
                spaceMouseSpeedX = _rawSpaceMouseX;
                spaceMouseSpeedY = _rawSpaceMouseY;
                spaceMouseSpeedZ = _rawSpaceMouseZ;
            }

            if (spaceMouseSpeedX != 0 || spaceMouseSpeedY != 0 || spaceMouseSpeedZ != 0)
            {
                double sensibilite = 0.05;

                _horizontalRotation.Angle -= spaceMouseSpeedY * elapsed * sensibilite;
                ClampVertical(_verticalRotation.Angle + (spaceMouseSpeedX * elapsed * sensibilite));

                MarquerMouvement();
            }

            // ────────────────────────────────────────────────────────────────────── 
            // ── 3. MOTEUR PHYSIQUE (Accélération & Décélération) ──────────────────
            double targetSpeed = 0;

            if (!_isMouseDown && !_isAutoClosingPhase)
            {
                if (_autoRotState == AutoRotationState.Lent)
                    targetSpeed = 360.0 / AutoRotateSlowSeconds;
                else if (_autoRotState == AutoRotationState.Normal)
                    targetSpeed = 360.0 / AutoRotateNormalSeconds;
                else if (_autoRotState == AutoRotationState.Rapide)
                    targetSpeed = 360.0 / AutoRotateFastSeconds;
            }

            // Application des forces de freinage ou d'accélération
            if (!_isMouseDown)
            {
                // 🛑 LE FREIN AÉRODYNAMIQUE DE LA JAMAIS CONTENTE :
                double actuelDecelRate = DecelerationRate;

                if (_isMouseInertia)
                {
                    double vitesseAbsolue = Math.Abs(_currentRotationSpeed);

                    // Formule physique : Frein linéaire de base + (Coefficient * Vitesse²)
                    // Le coefficient 0.02 est notre "profil aérodynamique" à ajuster.
                    actuelDecelRate = DecelerationRate + (0.02 * vitesseAbsolue * vitesseAbsolue);
                }

                if (_currentRotationSpeed < targetSpeed)
                {
                    // Si la vitesse est négative (élan de la souris vers la gauche), 
                    // on applique notre super-frein dynamique pour remonter vers 0
                    double rate = (_currentRotationSpeed < 0) ? actuelDecelRate : AccelerationRate;
                    _currentRotationSpeed += rate * elapsed;

                    if (_currentRotationSpeed >= targetSpeed)
                    {
                        _currentRotationSpeed = targetSpeed;
                        _isMouseInertia = false; // L'élan de la souris est totalement amorti
                    }
                }
                else if (_currentRotationSpeed > targetSpeed)
                {
                    // Freinage standard (élan vers la droite ou décélération d'autorotation)
                    _currentRotationSpeed -= actuelDecelRate * elapsed;

                    if (_currentRotationSpeed <= targetSpeed)
                    {
                        _currentRotationSpeed = targetSpeed;
                        _isMouseInertia = false; // L'élan de la souris est totalement amorti
                    }
                }
            }

            // ──────────────────────────────────────────────────────────────────────
            // ── 4. APPLICATION DE L'ÉNERGIE CINÉTIQUE HORIZONTALE ─────────────────
            if (Math.Abs(_currentRotationSpeed) > 0.01)
            {
                _horizontalRotation.Angle = (_horizontalRotation.Angle + (_currentRotationSpeed * elapsed)) % 360;

                if (_horizontalRotation.Angle < 0) _horizontalRotation.Angle += 360;

                if (_context != null && !_context.BlocageShowCaption)
                {
                    _context.BlocageShowCaption = true;
                }

                MarquerMouvement();

                // ── Analyse de l'AutoClose en cours de route ──────────────────
                if (_autoCloseActive && _autoCloseTargetAngle >= 0 && !_isAutoClosingPhase)
                {
                    double angleActuel = _horizontalRotation.Angle;
                    double diffAngulaire = Math.Abs(angleActuel - _autoCloseStartAngle);

                    // 🎯 Calcul linéaire parfait du parcours de 0° à 360° pour l'icône de l'anneau
                    double angleParcouru = (angleActuel - _autoCloseStartAngle + 360) % 360;
                    rectProgressTransform.Angle = angleParcouru;

                    if (!_hasLeftStartZone && (diffAngulaire > 6.0 && diffAngulaire < 354.0))
                    {
                        _hasLeftStartZone = true;
                    }

                    // ⏱️ GESTION DU POPUP TEMPOREL (Anticipation de 1.5 seconde avant l'arrêt)
                    if (_hasLeftStartZone && !_isPopupShown && _currentRotationSpeed > 0)
                    {
                        double angleRestant = 360.0 - angleParcouru;
                        double tempsRestant = angleRestant / _currentRotationSpeed;

                        if (tempsRestant <= 1.5) // Déclenchement à T-1.5s exacts !
                        {
                            _isPopupShown = true;
                            txtInfoPopup.Text = "🚪 AutoClose...";
                            (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                        }
                    }

                    // Seuil d'entrée physique dans la zone d'arrêt (inchangé)
                    if (_hasLeftStartZone && diffAngulaire <= 2.0)
                    {
                        _isAutoClosingPhase = true;
                    }
                }
            }

            // ── 5. ARRÊT DE L'AUTOCLOSE (Fermeture de la fenêtre une fois au stand) ──
            if (_isAutoClosingPhase && Math.Abs(_currentRotationSpeed) <= 0.05)
            {
                _autoCloseActive = false;
                _isAutoClosingPhase = false;
                _autoRotState = AutoRotationState.Off;

                Window.GetWindow(this)?.Close();
                return;
            }
            if (_currentRotationSpeed == 0 && _context != null && _context.BlocageShowCaption)
            {
                _context.BlocageShowCaption = false;
            }
            // ──────────────────────────────────────────────────────────────────────
            // ─── 6. GESTION DE LA BARRE DE BOUTONS ────────────────────────────────
            UpdateBarreOpacity();
        }
        // Note l'heure courante comme "dernier mouvement détecté".
        private void MarquerMouvement()
        {
            _lastMovementTime = DateTime.Now;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Contraint une valeur entre un minimum et un maximum.</summary>
        private static double Clamp(double value, double min, double max)
            => value < min ? min : value > max ? max : value;

        /// <summary>Applique la contrainte verticale à l'angle de la caméra.</summary>
        private void ClampVertical(double newAngle)
            => _verticalRotation.Angle = Clamp(newAngle, VerticalAngleMin, VerticalAngleMax);

        // ─────────────────────────────────────────────────────────────────────
        // Génération de la sphère 3D (mesh)
        // ─────────────────────────────────────────────────────────────────────
        private static MeshGeometry3D CreatePanoramaSphere()
        {
            var mesh = new MeshGeometry3D();

            // Calcul des sommets et des coordonnées de texture (uv-mapping sphérique)
            for (int stack = 0; stack <= SphereStacks; stack++)
            {
                double phi = Math.PI / SphereStacks * stack;
                for (int slice = 0; slice <= SphereSlices; slice++)
                {
                    double theta = 2 * Math.PI / SphereSlices * slice;
                    double x = Math.Sin(phi) * Math.Cos(theta);
                    double y = Math.Cos(phi);
                    double z = Math.Sin(phi) * Math.Sin(theta);

                    mesh.Positions.Add(new Point3D(x, y, z));
                    mesh.TextureCoordinates.Add(new Point(
                        slice / (double)SphereSlices,
                        phi / Math.PI));
                }
            }

            // Construction des triangles (deux triangles par quad de la grille)
            for (int stack = 0; stack < SphereStacks; stack++)
            {
                for (int slice = 0; slice < SphereSlices; slice++)
                {
                    int i0 = stack * (SphereSlices + 1) + slice;
                    int i1 = (stack + 1) * (SphereSlices + 1) + slice;
                    int i2 = stack * (SphereSlices + 1) + slice + 1;
                    int i3 = (stack + 1) * (SphereSlices + 1) + slice + 1;

                    mesh.TriangleIndices.Add(i0);
                    mesh.TriangleIndices.Add(i2);
                    mesh.TriangleIndices.Add(i1);

                    mesh.TriangleIndices.Add(i2);
                    mesh.TriangleIndices.Add(i3);
                    mesh.TriangleIndices.Add(i1);
                }
            }

            return mesh;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Chargement de la texture panoramique
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Identique au code original : UriSource + CacheOption.OnLoad.
        /// Pas de MemoryStream, pas de Freeze.
        /// </summary>
        private static Material CreatePanoramaMaterial(string imagePath)
        {
            var image = new BitmapImage();
            try
            {
                image.BeginInit();
                image.UriSource = new Uri(imagePath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur chargement image 360° : " + ex.Message);
                return new DiffuseMaterial(new SolidColorBrush(Colors.DimGray));
            }

            var brush = new ImageBrush(image)
            {
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                TileMode = TileMode.FlipX,
                Stretch = Stretch.Fill
            };

            // Force un filtrage fluide et matériel de la texture (dernière modifi de gemini, peut-etre pas utile)
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);

            return new DiffuseMaterial(brush);
        }

        // ─────────────────────────────────────────────────────────────────────
        // IDisposable — nettoyage des ressources
        // ─────────────────────────────────────────────────────────────────────
        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            DeconnecterSpaceMouse();  // Coupe la connexion proprement

            //_spaceMouseManager?.Dispose();

            viewport3D.Children.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bascule plein écran (G47S53)
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Invoque la méthode ToggleFullscreen() de la fenêtre parente par
        /// réflexion, car elle n'est pas exposée dans une interface publique.
        /// </summary>
        private void ToggleFullscreen()
        {
            var window = Window.GetWindow(this);
            if (window == null) return;

            var method = window.GetType().GetMethod(
                "ToggleFullscreen",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            method?.Invoke(window, null);
        }
    }
}
