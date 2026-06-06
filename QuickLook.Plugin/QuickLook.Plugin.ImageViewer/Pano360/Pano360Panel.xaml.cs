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
//using System.Threading.Tasks;
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

        // ─────────────────────────────────────────────────────────────────────
        // > Champs SpaceMouse 3Dconnexion
        // ─────────────────────────────────────────────────────────────────────
        private Device _smDevice;
        private Sensor _smSensor;
        private bool _spaceMouseEnabled = false;

        // ─────────────────────────────────────────────────────────────────────
        // > Autorotation
        // ─────────────────────────────────────────────────────────────────────
        private enum AutoRotationState { Off, Lent, Normal, Rapide }

        // État courant de l'autorotation (commence arrêté).
        private AutoRotationState _autoRotState = AutoRotationState.Off;

        // ── Variables pour l'autorotation haute précision ───────────────────
        private System.Diagnostics.Stopwatch _autoRotateStopwatch = new ();
        private double _angleAuDemarrage = 0;

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
        }

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

        // ─────────────────────────────────────────────────────────────────────
        // Enregistrement des gestionnaires d'événements
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
                // Si l'autorotation est active, on la désactive et on arrête le chrono
                _autoRotState = AutoRotationState.Off;
                btnAutoRotate.Content = "⏸ Off";
                _autoRotateStopwatch.Stop();
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
            if (_context != null) _context.BlocageShowCaption = false;

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

            // ── Navigation souris ──────────────────────────────────────────
            if (_isMouseDown)
            {
                double deltaX = _lastMouseX - _startMouseX;
                double deltaY = _lastMouseY - _startMouseY;

                double speedX = Math.Abs(deltaX) < MouseDeadZone ? 0 : deltaX * MouseSensitivity * elapsed;
                double speedY = Math.Abs(deltaY) < MouseDeadZone ? 0 : deltaY * MouseSensitivity * elapsed;

                _horizontalRotation.Angle += speedX;
                ClampVertical(_verticalRotation.Angle - speedY);
            }

            // ── Autorotation (Solution Haute Précision) ────────────────────
            if (_autoRotState != AutoRotationState.Off && !_isMouseDown)
            {
                double secondsPerTurn;

                if (_autoRotState == AutoRotationState.Lent)
                    secondsPerTurn = AutoRotateSlowSeconds;
                else if (_autoRotState == AutoRotationState.Normal)
                    secondsPerTurn = AutoRotateNormalSeconds;
                else // Rapide
                    secondsPerTurn = AutoRotateFastSeconds;

                // Calcul mathématique rigide basé sur le temps total du chrono
                double tempsTotalEcoule = _autoRotateStopwatch.Elapsed.TotalSeconds;
                double nouvelAngle = _angleAuDemarrage + (360.0 * (tempsTotalEcoule / secondsPerTurn));

                // On applique l'angle parfait (le % 360 évite que le chiffre grandisse à l'infini)
                _horizontalRotation.Angle = nouvelAngle % 360;

                if (_context != null && !_context.BlocageShowCaption)
                {
                    _context.BlocageShowCaption = true;
                }

                MarquerMouvement(); // Indique à la boucle que l'autorotation compte comme un mouvement
            }

            // Gestion de la barre haute
            UpdateBarreOpacity();
        }
        private void MarquerMouvement()
        {
            // Note l'heure courante comme "dernier mouvement détecté".
            _lastMovementTime = DateTime.Now;
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
            // Passage à l'état suivant dans le cycle
            switch (_autoRotState)
            {
                case AutoRotationState.Off:
                    _autoRotState = AutoRotationState.Lent;
                    btnAutoRotate.Content = "🐢 Lent";
                    _angleAuDemarrage = _horizontalRotation.Angle;  // On mémorise le nouvel angle et on réinitialise le chrono
                    _autoRotateStopwatch.Restart();
                    break;

                case AutoRotationState.Lent:
                    _autoRotState = AutoRotationState.Normal;
                    btnAutoRotate.Content = "▶️ Normal";
                    _angleAuDemarrage = _horizontalRotation.Angle;  // On mémorise le nouvel angle et on réinitialise le chrono
                    _autoRotateStopwatch.Restart();
                    break;

                case AutoRotationState.Normal:
                    _autoRotState = AutoRotationState.Rapide;
                    btnAutoRotate.Content = "▶️▶️ Rapide";
                    _angleAuDemarrage = _horizontalRotation.Angle;  // On mémorise le nouvel angle et on réinitialise le chrono
                    _autoRotateStopwatch.Restart();
                    break;

                case AutoRotationState.Rapide:
                    _autoRotState = AutoRotationState.Off;
                    btnAutoRotate.Content = "⏸ Off";
                    _autoRotateStopwatch.Stop();  // On arrête le chrono
                    _lastMovementTime = DateTime.Now;
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SpaceMouse 3Dconnexion
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
                _smDevice = (Device)Activator.CreateInstance(
                    Type.GetTypeFromProgID("TDxInput.Device"));
                _smSensor = _smDevice.Sensor;
                _smSensor.SensorInput += OnSpaceMouseMouvement;
                _smDevice.Connect();
                _spaceMouseEnabled = true;
            }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    btnSpaceMouse.IsChecked = false;
                    btnSpaceMouse.IsEnabled = false;
                    btnSpaceMouse.Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210));
                    txtSpaceMouse.TextDecorations = TextDecorations.Strikethrough;
                    btnSpaceMouse.ToolTip = "SpaceMouse non disponible";
                });
            }
        }

        private void DeconnecterSpaceMouse()
        {
            if (!_spaceMouseEnabled || _smDevice == null) return;

            _smSensor.SensorInput -= OnSpaceMouseMouvement;
            _smDevice.Disconnect();
            _smDevice = null;
            _smSensor = null;
            _spaceMouseEnabled = false;
        }

        private void OnSpaceMouseMouvement()
        {
            if (_smSensor == null) return;

            double rx = _smSensor.Rotation.X;
            double ry = _smSensor.Rotation.Y;

            // ToDo: A remplacer par quelque chose de plus performant car on observe des saccades
            Dispatcher.Invoke(() =>
            {
                _horizontalRotation.Angle -= ry;
                ClampVertical(_verticalRotation.Angle + rx);

                // La SpaceMouse est en train de bouger → on marque le mouvement
                MarquerMouvement();
            });
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
            DeconnecterSpaceMouse();
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
                    _isBarreMasquee = true;
                }
            }
            else
            {
                // Le panorama est à l'arrêt complet depuis un moment : on réaffiche (FadeIn)
                if (_isBarreMasquee)
                {
                    (barreBtn.Resources["FadeInBarreBtn"] as Storyboard)?.Begin(barreBtn);
                    _isBarreMasquee = false;
                }
            }
        }
    }
}
