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
using Newtonsoft.Json;

namespace QuickLook.Plugin.ImageViewer.Pano360
{
    public partial class Pano360Panel : UserControl, IDisposable
    {
        #region Constantes et Déclarations de champs

        // ─────────────────────────────────────────────────────────────────────
        // > Référence au contexte QuickLook et au chemin du fichier image
        // ─────────────────────────────────────────────────────────────────────
        private readonly QuickLook.Common.Plugin.ContextObject _context;
        private readonly string _imagePath;

        // ─────────────────────────────────────────────────────────────────────
        // > Sauvegarde des préférences (OPTIONS)
        // ─────────────────────────────────────────────────────────────────────
        // Attention d'autres options sont aussi dispo dans la section "Mode Tour Unique de démarrage avec accélération/décélération"
        private readonly string _configPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickLook",
        "Pano360Settings.json");

        private bool _chkFullscreenSavedValue = false;
        private int _StartAutoRotate = 1;
        private int _StartRadioBouton = 1;
        private bool _StartOneTurnActive = false;
        private double _OptionFov = 90.0;
        private bool _OptionOpen = false;
        private bool _OptionBarreReduite = false;

        private Pano360Settings settings;

        // Structure pour le stockage des options
        public class Pano360Settings
        {
            public double AutoRotateSlowSeconds { get; set; } = 60.0;
            public double AutoRotateNormalSeconds { get; set; } = 20.0;
            public double AutoRotateFastSeconds { get; set; } = 10.0;
            public bool FullscreenStartup { get; set; } = false;
            public bool StartBarreReduite { get; set; } = false;
            public double DefaultFov { get; set; } = 90.0;
            public int StartRadioBouton { get; set; } = 1;
            public int StartAutoRotate { get; set; } = 1;
            public bool StartOneTurnAndClose { get; set; } = false; // Le Flag de démarrage
            public double StartOneTurnDuration { get; set; } = 15.0; // Durée par défaut (ex: 15s)
        }

        // ─────────────────────────────────────────────────────────────────────
        // > Constantes — paramètres de la sphère et de la navigation
        // ─────────────────────────────────────────────────────────────────────
        private const int SphereSlices = 72;
        private const int SphereStacks = 36;

        private const double FovMin = 30.0;
        private const double FovMax = 135.0;
        private const double FovDefault = 90.0;
        private const double FovZoomStep = 5.0;

        private const double MouseSensitivity = 1.0;
        private const double MouseDeadZone = 5.0;

        private const double VerticalAngleMin = -90.0;
        private const double VerticalAngleMax = 45.0;

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
        private bool _isMouseInertia = false;    // Indique si le mouvement résiduel vient d'un lancer de souris
        private bool _isPopupShown = false;      // Évite les déclenchements multiples du popup avant la fermeture
        private double _targetFov = FovDefault;  // Implémentation d'un comportement en douceur de la roulette
        private bool _isFovAnimating = false;

        // ─────────────────────────────────────────────────────────────────────
        // > Champs SpaceMouse 3Dconnexion
        // ─────────────────────────────────────────────────────────────────────
        private Device _smDevice;
        private Sensor _smSensor;
        private TDxInput.Keyboard _keyboardSpaceMouse;

        private double sensibiliteTangage = 0.05;     //X, Pitch
        private double sensibiliteLacet = 0.05;       //Y, Yaw 
        //private double sensibiliteRoulis = 0.002;     //Z, Roll

        private bool _spaceMouseEnabled = false;

        private readonly object _spaceMouseLock = new object();
        private double _rawSpaceMouseX = 0;
        private double _rawSpaceMouseY = 0;
        private double _rawSpaceMouseZ = 0;
        private double _rawSpaceMouseZoom = 0;

        private const double ZoomDeadZone = 50.0;     // Large zone morte                           (début:100)
        private const double ZoomSensitivity = 0.05;  // Pas extrêmement faible                     (début:0.01)
        private const double ZoomFreeSpeedXY = 1000;  // À monter si le zoom se bloque trop souvent (début:50)

        // ─────────────────────────────────────────────────────────────────────
        // > Raccourcis Clavier SpaceMouse — navigation vers angle cible
        // ─────────────────────────────────────────────────────────────────────
        private double _targetHorizontalAngle = double.NaN; // NaN = pas de cible active
        private double _targetVerticalAngle = double.NaN;
        private const double KeySnapSpeed = 180.0;          // °/s de rotation animée
        private double _homeHorizontalAngle = 0.0;          // Angle de départ mémorisé
        private double _homeVerticalAngle = 0.0;

        // ─────────────────────────────────────────────────────────────────────
        // > Autorotation
        // ─────────────────────────────────────────────────────────────────────
        private enum AutoRotationState { Off, Lent, Normal, Rapide }

        // État courant de l'autorotation (commence arrêté).
        private AutoRotationState _autoRotState = AutoRotationState.Off;

        // Durée d'un tour complet (360°) en secondes.
        // Changez ces valeurs pour accélérer ou ralentir chaque mode.
        private double AutoRotateFastSeconds = 10.0;   // Tour rapide   : 10 s
        private double AutoRotateNormalSeconds = 20.0; // Tour normal   : 20 s
        private double AutoRotateSlowSeconds = 60.0;   // Tour lent     : 60 s

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
        // > Mode Tour Unique de démarrage avec accélération/décélération
        // ─────────────────────────────────────────────────────────────────────
        private bool _oneTurnActive = false;
        private double _oneTurnTimer = 0.0;
        private double _oneTurnDuration = 15.0; // Récupéré depuis les settings
        private double _oneTurnStartAngle = 0.0;
        private double _oneTurnAccelTime = 2.5;  // Durée des phases d'accel/decel en secondes
        private double _oneTurnVMax = 0.0;
        private double _oneTurnAccelRate = 0.0;

        // ─────────────────────────────────────────────────────────────────────
        // > barre de boutons
        // ─────────────────────────────────────────────────────────────────────
        // Délai d'inactivité (en secondes) avant de réafficher les boutons.
        private const double InactivityDelay = 0.5;
        private bool _isBarreCompactee = false;

        // ─────────────────────────────────────────────────────────────────────
        // > Opacité progressive
        // ─────────────────────────────────────────────────────────────────────
        // Horodatage du dernier mouvement détecté (souris OU autorotation OU SpaceMouse).
        private DateTime _lastMovementTime = DateTime.MinValue;

        // Indique si un mouvement était actif au frame précédent.
        // Sert à détecter le passage repos ↔ mouvement sans heuristique trop lourde.

        private bool _isBarreMasquee = false;
        private bool _isMouseOverBarre = false;

        #endregion Constantes et Déclarations de champs

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

            // AutoClose désactivé par défaut au démarrage
            MajEtatAutoClose();

            // Charger et appliquer les options utilisateurs
            LoadSettings();
        }
        // ─────────────────────────────────────────────────────────────────────
        // Scène 3D
        // ─────────────────────────────────────────────────────────────────────
        private void InitScene()
        {
            SetupCamera();
            SetupLight();
            var material = CreatePanoramaMaterial(_imagePath);
            SetupSphere(material);
            SetupEventHandlers();

            // ── Mémorisation de la vue "Home" ──
            _homeHorizontalAngle = _horizontalRotation.Angle; // 0° au démarrage
            _homeVerticalAngle = _verticalRotation.Angle;     // 0° au démarrage

            txtLoading.Visibility = Visibility.Collapsed;
            _context.IsBusy = false;  // Signale à QuickLook que le chargement est terminé

            Focus();

            // ── Connexion SpaceMouse une fois la scène complètement prête ──
            ConnecterSpaceMouse();
            btnSpaceMouse.Checked -= BtnSpaceMouse_Checked;
            btnSpaceMouse.IsChecked = true; // Reflète l'état visuel du bouton uniquement
            btnSpaceMouse.Checked += BtnSpaceMouse_Checked;
        }
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
        private void SetupLight()
        {
            viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new AmbientLight(Colors.White)
            });
        }
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
        // BOUCLE DE RENDU — appelée à chaque frame WPF
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

            // Partie roulette de la souris, transition douce
            if (Math.Abs(_camera.FieldOfView - _targetFov) > 0.05)
            {
                double fovDiff = _targetFov - _camera.FieldOfView;
                _camera.FieldOfView += fovDiff * Math.Min(12.0 * elapsed, 1.0);
                txtFov.Text = string.Format("(FOV: {0:F0}°)", _camera.FieldOfView);

                if (!_isFovAnimating)
                {
                    _isFovAnimating = true;
                    if (txtFov.Resources["FadeFovStoryboard"] is Storyboard sb)
                        sb.Begin(txtFov);
                }
            }
            else
            {
                _camera.FieldOfView = _targetFov;
                _isFovAnimating = false;
            }

            // ──────────────────────────────────────────────────────────────────────
            // ── 1b. NAVIGATION SPACEMOUSE (Directe & Instinctive) ─────────────────
            double spaceMouseSpeedX = 0;
            double spaceMouseSpeedY = 0;
            double spaceMouseSpeedZ = 0;
            double spaceMouseZoom = 0;

            lock (_spaceMouseLock)
            {
                spaceMouseSpeedX = _rawSpaceMouseX;
                spaceMouseSpeedY = _rawSpaceMouseY;
                spaceMouseSpeedZ = _rawSpaceMouseZ;
                spaceMouseZoom = _rawSpaceMouseZoom;
            }

            // Partie rotation du panorama
            if (spaceMouseSpeedX != 0 || spaceMouseSpeedY != 0 || spaceMouseSpeedZ != 0)
            {
                if (_autoRotState == AutoRotationState.Off && !_oneTurnActive)
                {
                    _horizontalRotation.Angle -= spaceMouseSpeedY * elapsed * sensibiliteLacet;
                }

                if (Math.Abs(spaceMouseSpeedY) > 500 && (_autoRotState != AutoRotationState.Off || _oneTurnActive))
                {
                    txtInfoPopup.Text = "Axe horizontale vérouillé en autorotation";
                    (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                }

                ClampVertical(_verticalRotation.Angle + (spaceMouseSpeedX * elapsed * sensibiliteTangage));

                MarquerMouvement();
            }

            // Partie Zoom via le déplacement du manche vers l'avant/arrière
            bool panoramaEnMouvement = Math.Abs(spaceMouseSpeedX) > ZoomFreeSpeedXY || Math.Abs(spaceMouseSpeedY) > ZoomFreeSpeedXY;

            if (!panoramaEnMouvement && Math.Abs(_rawSpaceMouseZoom) > ZoomDeadZone)
            {
                _targetFov = Clamp(_targetFov + _rawSpaceMouseZoom * ZoomSensitivity * elapsed, FovMin, FovMax);
            }

            // ──────────────────────────────────────────────────────────────────────
            // ── 1c. CLAVIER SPACEMOUSE — interpolation vers angle cible ───────────
            if (!double.IsNaN(_targetHorizontalAngle))
            {
                double diff = AngleDiff(_targetHorizontalAngle, _horizontalRotation.Angle);

                if (Math.Abs(diff) < 0.2)
                {
                    // Arrivé à destination : on verrouille et on efface la cible
                    _horizontalRotation.Angle = _targetHorizontalAngle;
                    _targetHorizontalAngle = double.NaN;
                    _currentRotationSpeed = 0;
                }
                else
                {
                    // Interpolation exponentielle (ease-out naturel)
                    double step = diff * Math.Min(KeySnapSpeed * elapsed / Math.Abs(diff), 1.0);
                    _horizontalRotation.Angle = NormalizeAngle(_horizontalRotation.Angle + step);
                    _currentRotationSpeed = 0; // Coupe l'inertie souris pendant le snap
                    MarquerMouvement();
                }
            }

            if (!double.IsNaN(_targetVerticalAngle))
            {
                double diff = _targetVerticalAngle - _verticalRotation.Angle;

                if (Math.Abs(diff) < 0.2)
                {
                    _verticalRotation.Angle = _targetVerticalAngle;
                    _targetVerticalAngle = double.NaN;
                }
                else
                {
                    double step = diff * Math.Min(KeySnapSpeed * elapsed / Math.Abs(diff), 1.0);
                    ClampVertical(_verticalRotation.Angle + step);
                    MarquerMouvement();
                }
            }

            // ────────────────────────────────────────────────────────────────────── 
            // ── 2. MOTEUR PHYSIQUE (Calcul des vitesses) ──────────────────────────
            // ────────────────────────────────────────────────────────────────────── 

            double targetSpeed = 0; // Déclarée ici au début du moteur physique

            if (_oneTurnActive)
            {
                // ── GESTION DU TOUR UNIQUE DE DÉMARRAGE ──
                if (_isMouseDown)
                {
                    // Si l’utilisateur touche à la souris ou SpaceMouse, on lui rend la main
                    txtInfoPopup.Text = "Annulation fermeture";
                    (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                    _oneTurnActive = false;
                    _autoCloseActive = false;
                    btnAutoRotate.Content = "Rotation auto.";
                    btnAutoRotate.IsEnabled = true;
                    MajEtatAutoClose();
                }
                else
                {
                    // ── Gestion de 1 tour et on ferme ──
                    // ───────────────────────────────────
                    _oneTurnTimer += elapsed;

                    // Profil de vitesse trapézoïdal
                    if (_oneTurnTimer <= _oneTurnAccelTime)
                    {
                        // ── Phase 1 : Accélération
                        _currentRotationSpeed = _oneTurnAccelRate * _oneTurnTimer;
                    }
                    else if (_oneTurnTimer >= (_oneTurnDuration - _oneTurnAccelTime))
                    {
                        // ── Phase 3 : Décélération
                        double tempsRestant = _oneTurnDuration - _oneTurnTimer;
                        if (tempsRestant < 0) tempsRestant = 0;
                        _currentRotationSpeed = _oneTurnAccelRate * tempsRestant;
                    }
                    else
                    {
                        // ── Phase 2 : Vitesse max constante
                        _currentRotationSpeed = _oneTurnVMax;
                    }

                    // Fin du chrono
                    if (_oneTurnTimer >= _oneTurnDuration)
                    {
                        _currentRotationSpeed = 0;
                        _oneTurnActive = false;

                        // Recalage parfait à 360°
                        _horizontalRotation.Angle = (_oneTurnStartAngle + 360.0) % 360;

                        // Fermeture propre
                        Window.GetWindow(this)?.Close();
                        return;
                    }
                }
            }
            else
            {
                // ── MODE STANDARD (Uniquement si le tour de démarrage est fini ou désactivé) ──

                if (!_isMouseDown && !_isAutoClosingPhase)
                {
                    // Autorotation classique (choix de targetSpeed selon l'état)
                    if (_autoRotState == AutoRotationState.Lent)
                        targetSpeed = 360.0 / AutoRotateSlowSeconds;
                    else if (_autoRotState == AutoRotationState.Normal)
                        targetSpeed = 360.0 / AutoRotateNormalSeconds;
                    else if (_autoRotState == AutoRotationState.Rapide)
                        targetSpeed = 360.0 / AutoRotateFastSeconds;
                    else                    
                        targetSpeed = 0;    
                }

                // Gestion de l'accélération / décélération vers targetSpeed 
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
            }

            // ────────────────────────────────────────────────────────────────────── 
            // ── 3. APPLICATION DE LA ROTATION (Commun aux deux modes) ─────────────
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

                    // 🎯 Icône de l'anneau : Calcul linéaire parfait du parcours de 0° à 360°
                    double angleParcouru = (angleActuel - _autoCloseStartAngle + 360) % 360;
                    rectProgressTransform.Angle = angleParcouru;

                    double angleRestant = 360.0 - angleParcouru;
                    double tempsRestant = angleRestant / _currentRotationSpeed;

                    System.Diagnostics.Debug.WriteLine($"Temp restant:{tempsRestant}");

                    if (!_hasLeftStartZone && (diffAngulaire > 6.0 && diffAngulaire < 354.0))
                    {
                        _hasLeftStartZone = true;
                    }

                    // ⏱️ GESTION DU POPUP TEMPOREL (Anticipation de 1.5 seconde avant l'arrêt)
                    if (_hasLeftStartZone && !_isPopupShown && _currentRotationSpeed > 0)
                    {
                        if (tempsRestant <= 1.5)  // Déclenchement à T-1.5s exacts !
                        {
                            if ((_oneTurnDuration < 6 && _oneTurnActive) || (AutoRotateFastSeconds < 6 && _autoRotState == AutoRotationState.Rapide)) 
                            {
                                _isPopupShown = true;
                                txtInfoPopup.Text = "Fermeture automatique...";
                                (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                            }
                            else
                            {
                                _isPopupShown = true;
                                txtInfoPopup.Text = "Fermeture automatique...";
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                            }
                        }
                    }

                    // Seuil d'entrée physique dans la zone d'arrêt (inchangé)
                    if (_hasLeftStartZone && diffAngulaire <= 2.0)
                    {
                        _isAutoClosingPhase = true;
                    }
                }
            }

            // ──────────────────────────────────────────────────────────────────────
            // ── 4. ARRÊT DE L'AUTOCLOSE (Fermeture de la fenêtre une fois au stand)
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
            // ── 5. GESTION DE LA BARRE DE BOUTONS ─────────────────────────────────
            UpdateBarreOpacity();
        }
        private void MarquerMouvement()
        {
            // Note l'heure courante comme "dernier mouvement détecté".
            _lastMovementTime = DateTime.Now;
        }
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

            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(new Action(() => Window.GetWindow(this)?.Close()));
                return; // On sort immédiatement du handler
            }

            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (_autoRotState != AutoRotationState.Off)
            {
                _autoRotState = AutoRotationState.Off;
                btnAutoRotate.Content = "Rotation auto.";
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
            // Pas adaptatif : quadratique, ~1° aux extrêmes, ~5° au centre
            double t = (_targetFov - FovMin) / (FovMax - FovMin);
            double step = 1.0 + 4.0 * (1.0 - Math.Abs(2.0 * t - 1.0));
            // step ≈ 1° à 30° et 135°, ≈ 5° à 90°

            double delta = e.Delta > 0 ? -step : step;
            _targetFov = Clamp(_targetFov + delta, FovMin, FovMax);
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

                _keyboardSpaceMouse = _smDevice.Keyboard;
                _keyboardSpaceMouse.KeyDown += OnSpaceMouseKeyDown;
                _keyboardSpaceMouse.KeyUp += OnSpaceMouseKeyUp;

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

                if (_keyboardSpaceMouse != null)
                {
                    _keyboardSpaceMouse.KeyDown -= OnSpaceMouseKeyDown;
                    _keyboardSpaceMouse.KeyUp -= OnSpaceMouseKeyUp;
                    _keyboardSpaceMouse = null;
                }

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
            // Vérifie si le capteur de la SpaceMouse est disponible. Si le capteur est nul, on interrompt l'exécution pour éviter une erreur de référence nulle.
            if (_smSensor == null) return;

            // Si le menu des options est ouvert, on interrompt l'exécution pour ignorer les mouvements de la SpaceMouse.
            if (_OptionOpen) return;

            try
            {
                lock (_spaceMouseLock)
                {
                    double axeX = _smSensor.Rotation.X;
                    double axeY = _smSensor.Rotation.Y;
                    double axeZ = _smSensor.Rotation.Z;
                    double intensite = _smSensor.Rotation.Angle;
                    double transY = _smSensor.Translation.Z; // Avant/arrière

                    // Debug complet avec les 3 axes pour y voir clair
                    System.Diagnostics.Debug.WriteLine($"SpaceMouse -> X:{axeX:F0} | Y:{axeY:F0} | Z:{axeZ:F0} | Angle:{intensite:F0}");

                    _rawSpaceMouseX = axeX * intensite;
                    _rawSpaceMouseY = axeY * intensite;
                    _rawSpaceMouseZ = axeZ * intensite;
                    _rawSpaceMouseZoom = transY;
                }
            }
            catch
            {
                // Sécurité COM
            }
        }
        private void OnSpaceMouseKeyDown(int keyCode)
        {
            //Debug
            System.Diagnostics.Debug.WriteLine($"[SpaceMouse] Touche pressée : {keyCode}");

            // Ignore si le panneau d'options est ouvert
            if (_OptionOpen) return;

            Dispatcher.Invoke(() =>
            {
                // |──────────────────────────────────────────────────
                // | Touches de la spacepilot pro (partie de droite) |
                // |                                                 |
                // |  ─────────────────────                          |
                // |  | 9/10    |     3/7 |                          |
                // |  |      ───────      |                          |
                // |  ───────|11/12|──────|                          |
                // |  |      ───────      |                          |
                // |  |  6/8    |     5/4 |                          |
                // |  ─────────────────────                          |
                // |                                                 |
                // |        Fit : 31 & 32                            |
                // ───────────────────────────────────────────────────
                switch (keyCode)
                {
                    case 9:
                        _targetHorizontalAngle = NormalizeAngle((double.IsNaN(_targetHorizontalAngle) ? _horizontalRotation.Angle : _targetHorizontalAngle) - 90.0);
                        break;
                    case 3:
                        _targetHorizontalAngle = NormalizeAngle((double.IsNaN(_targetHorizontalAngle) ? _horizontalRotation.Angle : _targetHorizontalAngle) + 90.0);
                        break;
                    case 6:
                        _targetHorizontalAngle = NormalizeAngle((double.IsNaN(_targetHorizontalAngle) ? _horizontalRotation.Angle : _targetHorizontalAngle) - _camera.FieldOfView);
                        break;
                    case 5:
                        _targetHorizontalAngle = NormalizeAngle((double.IsNaN(_targetHorizontalAngle) ? _horizontalRotation.Angle : _targetHorizontalAngle) + _camera.FieldOfView);
                        break;
                    case 11:
                        AutoRotate_ActionBouton();
                        AfficheEtatAutoRotation();
                        break;
                    case 32:
                        _targetHorizontalAngle = _homeHorizontalAngle;
                        _targetVerticalAngle = _homeVerticalAngle;
                        //e.Handled = true;
                        break;


                }
            });
        }
        private void OnSpaceMouseKeyUp(int keyCode)
        {

        }

        // ─────────────────────────────────────────────────────────────────────
        // BOUTONS OPTIONS & PREFERENCES
        // ─────────────────────────────────────────────────────────────────────
        private void LoadSettings()
        {
            try
            {
                if (System.IO.File.Exists(_configPath))
                {
                    string json = System.IO.File.ReadAllText(_configPath);
                    settings = Newtonsoft.Json.JsonConvert.DeserializeObject<Pano360Settings>(json);
                    if (settings != null)
                    {
                        // ✅ Paramètres non-visuels : application immédiate
                        // Attribution des vitesses de l'autorotation
                        AutoRotateFastSeconds = settings.AutoRotateFastSeconds;
                        AutoRotateNormalSeconds = settings.AutoRotateNormalSeconds;
                        AutoRotateSlowSeconds = settings.AutoRotateSlowSeconds;
                        sldFast.Value = AutoRotateFastSeconds;
                        sldNormal.Value = AutoRotateNormalSeconds;
                        sldSlow.Value = AutoRotateSlowSeconds;

                        // Attribution des autres options aux variables locales
                        _chkFullscreenSavedValue = settings.FullscreenStartup;
                        _OptionBarreReduite = settings.StartBarreReduite;
                        _OptionFov = settings.DefaultFov;

                        _StartRadioBouton = settings.StartRadioBouton;
                        _StartAutoRotate = settings.StartAutoRotate;
                        _StartOneTurnActive = settings.StartOneTurnAndClose;
                        _oneTurnDuration = settings.StartOneTurnDuration;

                        // Mise à jour boite de dialogue: Fullscreen
                        chkFullscreen.IsChecked = _chkFullscreenSavedValue;

                        // Mise à jour boite de dialogue: Barre reduite
                        chkBarreReduite.IsChecked = _OptionBarreReduite;

                        // Mise à jour boite de dialogue: Fov
                        _targetFov = _OptionFov;
                        sldFov.Value = _OptionFov;
                        txtFov.Text = string.Format("(FOV: {0:F0}°)", _OptionFov);

                        // Mise à jour boite de dialogue: durée autoclose
                        sldAutoCloseDelay.Value = _oneTurnDuration;
                        lblAutoCloseDelayValue.Text = _oneTurnDuration.ToString() +"s";

                        // Mise à jour boite de dialogue: boutonOptionAutoRotate
                        switch (_StartAutoRotate)
                        {
                            case 1:
                                txtOptionAutoRotate.Text = "🐢 Lent";
                                break;
                            case 2:
                                txtOptionAutoRotate.Text = "▶️ Normal";
                                break;
                            case 3:
                                txtOptionAutoRotate.Text = "▶️▶️ Rapide";
                                break;
                        }
                        // Mise à jour boite de dialogue: RadioBouton
                        switch (_StartRadioBouton)
                        {
                            case 1:
                                radStartNormal.IsChecked = true;
                                _StartOneTurnActive = false;

                                btnOptionAutoRotate.Visibility = Visibility.Hidden;
                                TextAutoClose.Visibility = Visibility.Hidden;
                                sldAutoCloseDelay.Visibility = Visibility.Hidden;
                                lblAutoCloseDelayValue.Visibility = Visibility.Hidden;
                                break;
                            case 2:
                                radStartAutoClose.IsChecked = true;
                                _StartOneTurnActive = false;

                                btnOptionAutoRotate.Visibility = Visibility.Visible;
                                TextAutoClose.Visibility = Visibility.Hidden;
                                sldAutoCloseDelay.Visibility = Visibility.Hidden;
                                lblAutoCloseDelayValue.Visibility = Visibility.Hidden;
                                break;
                            case 3:
                                radStartAutoClose.IsChecked = true;
                                _StartOneTurnActive = true;

                                btnOptionAutoRotate.Visibility = Visibility.Hidden;
                                TextAutoClose.Visibility = Visibility.Visible;
                                sldAutoCloseDelay.Visibility = Visibility.Visible;
                                lblAutoCloseDelayValue.Visibility = Visibility.Visible;
                                break;
                        }

                        // ✅ Paramètres visuels : différés après chargement complet de la fenêtre
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // FOV
                            _camera.FieldOfView = _OptionFov;
                            
                            // Fullscreen
                            if (_chkFullscreenSavedValue) ToggleFullscreen();

                            // ── Application de la barre réduite au démarrage ─────────────────
                            if (settings.StartBarreReduite)
                            {
                                _isBarreCompactee = true;

                                // On bloque l'affichage de manière instantanée sans lancer l'animation
                                grpBarreGauche.MaxWidth = 0;
                                grpBarreDroite.MaxWidth = 0;
                                grpBarreGauche.Opacity = 0;
                                grpBarreDroite.Opacity = 0;

                                grpBarreGauche.ClipToBounds = true;
                                grpBarreDroite.ClipToBounds = true;
                            }

                            // ── Application de l'autorotation si sélectionnée ────────────────
                            if (_StartRadioBouton == 2)
                            {
                                switch (_StartAutoRotate)
                                {
                                    case 1:
                                        _autoRotState = AutoRotationState.Lent;
                                        btnAutoRotate.Content = "🐢 Lent";
                                        break;
                                    case 2:
                                        _autoRotState = AutoRotationState.Normal;
                                        btnAutoRotate.Content = "▶️ Normal";
                                        break;
                                    case 3:
                                        _autoRotState = AutoRotationState.Rapide;
                                        btnAutoRotate.Content = "▶️▶️ Rapide";
                                        break;
                                }
                                AfficheEtatAutoRotation();
                            }

                            // ── Application de 1 tour et on ferme ────────────────────────────
                            if (_StartOneTurnActive)
                            {
                                _oneTurnActive = true;
                                //_oneTurnDuration = settings.StartOneTurnDuration;
                                _oneTurnTimer = 0.0;
                                _oneTurnStartAngle = _horizontalRotation.Angle;

                                // Sécurité au cas où la durée entrée est trop courte pour le profil trapézoïdal
                                if (_oneTurnDuration <= _oneTurnAccelTime * 2)
                                {
                                    _oneTurnAccelTime = _oneTurnDuration / 2.0;
                                }

                                // Calcul des lois physiques adaptées au temps imposé
                                _oneTurnVMax = 360.0 / (_oneTurnDuration - _oneTurnAccelTime);
                                _oneTurnAccelRate = _oneTurnVMax / _oneTurnAccelTime;

                                // Texte d'information pour l'utilisateur
                                if (_oneTurnDuration > 6)
                                {
                                    txtInfoPopup.Text = "Rotation 1 tour + fermeture";
                                    (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                                }
                                else 
                                {
                                    txtInfoPopup.Text = "Rotation 1 tour + fermeture";
                                    (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                                }

                                // Bouton Autoclose : activation et mise à jour de l'affichage
                                _autoCloseActive = true;
                                txtAutoClose.Opacity = 1.0;
                                txtAutoClose.Text = "AutoClose: On";
                                txtAutoClose.TextDecorations = null;                      // Pas barré
                                panelAutoCloseProgress.Visibility = Visibility.Visible;   // 🟢 Visible si AutoClose Actif

                                // Affiche un texte différent sur le bouton RotationAuto
                                btnAutoRotate.Content = "Mode 1 tour";
                                btnAutoRotate.IsEnabled = false;

                                // Mise à jour de l'info popup sur la durée de l'autorotation
                                _autoCloseStartAngle = _horizontalRotation.Angle;
                                _autoCloseTargetAngle = _autoCloseStartAngle;
                                _hasLeftStartZone = false; // On vient juste d'arriver sur l'angle
                                _isPopupShown = false; // 🔄 Réinitialisation
                            }
                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                }
                else
                {
                    settings = new Pano360Settings();
                }
            }
            catch
            {
                settings = new Pano360Settings();
            }
        }
        private void SaveSettings()
        {
            // Assigner les variables physiques locales pour exécution immédiate
            AutoRotateSlowSeconds = sldSlow.Value;
            AutoRotateNormalSeconds = sldNormal.Value;
            AutoRotateFastSeconds = sldFast.Value;

            if (_StartRadioBouton == 3)
            {
                _StartOneTurnActive = true;
            }
            else
            {
                _StartOneTurnActive = false;
            }

            try
            {
                settings = new Pano360Settings
                {
                    AutoRotateSlowSeconds = (int)sldSlow.Value,
                    AutoRotateNormalSeconds = (int)sldNormal.Value,
                    AutoRotateFastSeconds = (int)sldFast.Value,
                    FullscreenStartup = chkFullscreen.IsChecked ?? false,
                    StartBarreReduite = chkBarreReduite.IsChecked ?? false,
                    DefaultFov = (int)sldFov.Value,
                

                    StartRadioBouton = _StartRadioBouton,
                    StartAutoRotate = _StartAutoRotate,
                    StartOneTurnAndClose = _StartOneTurnActive,
                    StartOneTurnDuration = (int)sldAutoCloseDelay.Value
                };

                //Serialisation et sauvegarde
                string directory = System.IO.Path.GetDirectoryName(_configPath);
                if (!System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(_configPath, json);
            }
            catch
            {
            }
        }
        private void BtnOptions_Click(object sender, RoutedEventArgs e)
        {
            // Arrêt immédiat de l'autorotation et de l'autoclose
            _autoRotState = AutoRotationState.Off;
            btnAutoRotate.Content = "Rotation auto.";
            _autoCloseActive = false;
            MajEtatAutoClose();

            // Arrêt immédiat de 1tour et on ferme
            _oneTurnActive = false;

            // Indique que la boite de dialogue d'option est ouverte (utile pour désactiver la spacemouse)
            _OptionOpen = true;
            
            // 🎯 ON ALLUME LE FLOU DERRIÈRE : Un rayon de 15 rend le panorama magnifiquement flou
            viewBlur.Radius = 15;
            
            // Affichage de l'incrustation des options
            gridPreferences.Visibility = Visibility.Visible;
        }
        private void RadioBtnMode_Click(object sender, RoutedEventArgs e)
        {
            if (radStartNormal.IsChecked == true)
            {
                _StartRadioBouton = 1;

                btnOptionAutoRotate.Visibility = Visibility.Hidden;
                TextAutoClose.Visibility = Visibility.Hidden;
                sldAutoCloseDelay.Visibility = Visibility.Hidden;
                lblAutoCloseDelayValue.Visibility = Visibility.Hidden;
            }
            else if (radStartAutoRotate.IsChecked == true)
            {
                _StartRadioBouton = 2;

                btnOptionAutoRotate.Visibility = Visibility.Visible;
                TextAutoClose.Visibility = Visibility.Hidden;
                sldAutoCloseDelay.Visibility = Visibility.Hidden;
                lblAutoCloseDelayValue.Visibility = Visibility.Hidden;
            }
            else if (radStartAutoClose.IsChecked == true)
            {
                _StartRadioBouton = 3;

                btnOptionAutoRotate.Visibility = Visibility.Hidden;
                TextAutoClose.Visibility = Visibility.Visible;
                sldAutoCloseDelay.Visibility = Visibility.Visible;
                lblAutoCloseDelayValue.Visibility = Visibility.Visible;
            }
        }
        private void BtnOptionAutoRotate_Click(object sender, RoutedEventArgs e)
        {
            switch (_StartAutoRotate)
            {
                case 1:
                    _StartAutoRotate = 2;
                    txtOptionAutoRotate.Text = "▶️ Normal";
                    break;
                case 2:
                    _StartAutoRotate = 3;
                    txtOptionAutoRotate.Text = "▶️▶️ Rapide";
                    break;
                case 3:
                    _StartAutoRotate = 1;
                    txtOptionAutoRotate.Text = "🐢 Lent";
                    break;
            }
        }
        private void BtnCloseOptions_Click(object sender, RoutedEventArgs e)
        {
            // 🎯 ON ÉTEINT LE FLOU : Le panorama redevient instantanément net
            viewBlur.Radius = 0;

            // Indique que la boite de dialogue d'option est fermée (utile pour activer la spacemouse)
            _OptionOpen = false;

            // Masquage de la boîte
            gridPreferences.Visibility = Visibility.Collapsed;

            // Sauvegarde définitive
            SaveSettings();
        }
        private void SldFov_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblFovValue != null) lblFovValue.Text = string.Format("{0:F0}°", e.NewValue);
        }
        private void SldAutoCloseDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblAutoCloseDelayValue != null) lblAutoCloseDelayValue.Text = string.Format("{0:F0}s", e.NewValue);
        }
        private void SldSlow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblSlow != null) lblSlow.Text = string.Format("{0:F0}s", e.NewValue);
        }
        private void SldNormal_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblNormal != null) lblNormal.Text = string.Format("{0:F0}s", e.NewValue);
        }
        private void SldFast_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblFast != null) lblFast.Text = string.Format("{0:F0}s", e.NewValue);
        }
        private void GridPreferences_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 🎯 On indique à WPF que l'événement s'arrête ici. 
            // Le clic est "consommé" par le fond noir et ne traverse pas vers le panorama !
            e.Handled = true;
        }
        private void GridPreferences_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 🎯 On bloque aussi la molette de la souris pour empêcher le zoom en arrière-plan
            e.Handled = true;
        }
        // ─────────────────────────────────────────────────────────────────────
        // BOUTON AUTOROTATION
        // ─────────────────────────────────────────────────────────────────────
        private void BtnAutoRotate_Click(object sender, RoutedEventArgs e)
        {
            AutoRotate_ActionBouton();
        }
        private void AutoRotate_ActionBouton()
        {
            // Accessible via le bouton de l'interface mais aussi via le bouton central de la spacemouse
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
                    btnAutoRotate.Content = "Rotation auto.";
                    _lastMovementTime = DateTime.Now;
                    break;
            }
            MajEtatAutoClose();
        }
        private void AfficheEtatAutoRotation() 
        {
            switch (_autoRotState)
            {
                case AutoRotationState.Off:
                    txtInfoPopup.Text = "Rotation automatique: Off";
                    break;

                case AutoRotationState.Lent:
                    txtInfoPopup.Text = "Rotation automatique: 🐢 Lent";
                    break;

                case AutoRotationState.Normal:
                    txtInfoPopup.Text = "Rotation automatique: ▶️ Normal";
                    break;

                case AutoRotationState.Rapide:
                    txtInfoPopup.Text = "Rotation automatique: ▶️▶️ Rapide";
                    break;
            }
            (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
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
                txtAutoClose.Text = "AutoClose: Off";
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
                    txtAutoClose.Text = "AutoClose: On";
                    txtAutoClose.TextDecorations = null;                      // Pas barré
                    panelAutoCloseProgress.Visibility = Visibility.Visible;   // 🟢 Visible si AutoClose Actif
                }
                else
                {
                    txtAutoClose.Text = "AutoClose: Off";
                    txtAutoClose.TextDecorations = null;                      // Pas barré
                    panelAutoCloseProgress.Visibility = Visibility.Collapsed; // 🛑 Masqué si désactivé
                }
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // Barre de BOUTONS
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
        private void TxtFov_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                MarquerMouvement();

                _isBarreCompactee = !_isBarreCompactee;

                // On active le ClipToBounds pour masquer proprement les boutons pendant qu'ils se font écraser
                grpBarreGauche.ClipToBounds = true;
                //grpBarreDroite.KeepInLayout = false; // Astuce facultative, gérée par le flux
                grpBarreDroite.ClipToBounds = true;

                // Détermination des dimensions de départ et d'arrivée
                // Si on compacte, on part de la taille actuelle vers 0.
                // Si on ouvre, on part de 0 vers 252 (la somme exacte de tes deux boutons de 120px + marges).
                double maxGaucheStart = _isBarreCompactee ? grpBarreGauche.ActualWidth : 0;
                double maxGaucheEnd = _isBarreCompactee ? 0 : 252;

                double maxDroiteStart = _isBarreCompactee ? grpBarreDroite.ActualWidth : 0;
                double maxDroiteEnd = _isBarreCompactee ? 0 : 252;

                // Animation de l'opacité pour accompagner la glissière
                double opaciteCible = _isBarreCompactee ? 0 : 1;

                IEasingFunction fonctionDouce = new QuarticEase { EasingMode = EasingMode.EaseOut };
                Duration dureeAnimation = new Duration(TimeSpan.FromMilliseconds(300));

                // Création des animations de Largeur Maximale
                DoubleAnimation animMaxGauche = new DoubleAnimation { From = maxGaucheStart, To = maxGaucheEnd, Duration = dureeAnimation, EasingFunction = fonctionDouce };
                DoubleAnimation animMaxDroite = new DoubleAnimation { From = maxDroiteStart, To = maxDroiteEnd, Duration = dureeAnimation, EasingFunction = fonctionDouce };

                DoubleAnimation animOpaciteG = new DoubleAnimation { To = opaciteCible, Duration = dureeAnimation };
                DoubleAnimation animOpaciteD = new DoubleAnimation { To = opaciteCible, Duration = dureeAnimation };

                // Libération des contraintes à la fin de l'ouverture pour garder l'adaptabilité du mode Auto
                if (!_isBarreCompactee)
                {
                    animMaxGauche.Completed += (s, args) => {
                        grpBarreGauche.BeginAnimation(StackPanel.MaxWidthProperty, null);
                        grpBarreGauche.MaxWidth = 500; // Largeur max par défaut confortable
                    };
                    animMaxDroite.Completed += (s, args) => {
                        grpBarreDroite.BeginAnimation(StackPanel.MaxWidthProperty, null);
                        grpBarreDroite.MaxWidth = 500;
                    };
                }

                // Exécution simultanée (un seul temps visuel)
                grpBarreGauche.BeginAnimation(StackPanel.MaxWidthProperty, animMaxGauche);
                grpBarreDroite.BeginAnimation(StackPanel.MaxWidthProperty, animMaxDroite);
                grpBarreGauche.BeginAnimation(StackPanel.OpacityProperty, animOpaciteG);
                grpBarreDroite.BeginAnimation(StackPanel.OpacityProperty, animOpaciteD);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static double Clamp(double value, double min, double max)
            // Contraint une valeur entre un minimum et un maximum.
            => value < min ? min : value > max ? max : value;
        private void ClampVertical(double newAngle)
            // Applique la contrainte verticale à l'angle de la caméra.
            => _verticalRotation.Angle = Clamp(newAngle, VerticalAngleMin, VerticalAngleMax);
        private static double NormalizeAngle(double angle)
        {
            // Ramène un angle dans [0, 360[
            angle %= 360.0;
            return angle < 0 ? angle + 360.0 : angle;
        }
        private static double AngleDiff(double target, double current)
        {
            // Retourne la différence signée la plus courte entre deux angles ([-180, +180])
            double diff = (target - current + 540.0) % 360.0 - 180.0;
            return diff;
        }
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
        // ─────────────────────────────────────────────────────────────────────
        // Dispose — nettoyage des ressources
        // ─────────────────────────────────────────────────────────────────────
        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            DeconnecterSpaceMouse();  // Coupe la connexion proprement

            viewport3D.Children.Clear();
        }
    }
}
