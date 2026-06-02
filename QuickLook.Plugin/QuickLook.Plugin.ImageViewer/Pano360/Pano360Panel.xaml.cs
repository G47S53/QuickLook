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

// ═══════════════════════════════════════════════════════════════════════════
// RÉSUMÉ DES MODIFICATIONS (Guillaume)
// ───────────────────────────────────────────────────────────────────────────
// 1. AUTOROTATION (3 états : Off / Lent / Rapide)
//    - Enum AutoRotationState avec les 3 valeurs.
//    - Constantes AutoRotateFastSeconds (durée d'un tour complet en mode Rapide)
//      et AutoRotateSlowSeconds (durée en mode Lent).
//    - BtnAutoRotate_Click() : cycle les états et met à jour le libellé du bouton.
//    - Dans OnRendering() : si une rotation est active, on incrémente
//      _horizontalRotation.Angle proportionnellement au temps écoulé.
//
// 2. OPACITÉ PROGRESSIVE sur mouvement (remplace barreBtn.Opacity = 0 / 1)
//    - _lastMovementTime : horodatage de la dernière détection de mouvement.
//    - _isMoving : indique si le panorama était en mouvement au dernier frame.
//    - UpdateBarreOpacity() : appelé à chaque frame dans OnRendering().
//      → Si mouvement détecté → fade-out vers OpacityMin.
//      → Si repos depuis InactivityDelay secondes → fade-in vers OpacityFull.
//    - "Mouvement" = souris pressée, autorotation active, ou SpaceMouse actif
//      avec déplacement détectable.
//    - Toutes les lignes "barreBtn.Opacity = 0/1" du code original ont été
//      supprimées ; c'est UpdateBarreOpacity() qui gère tout.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Threading.Tasks;
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
        // ─────────────────────────────────────────────────────────────────────
        // Constantes — paramètres de la sphère et de la navigation
        // ─────────────────────────────────────────────────────────────────────
        private const int SphereSlices = 72;
        private const int SphereStacks = 36;

        private const double FovMin = 30.0;
        private const double FovMax = 120.0;
        private const double FovDefault = 90.0;
        private const double FovZoomStep = 5.0;

        private const double MouseSensitivity = 1.0;
        private const double MouseDeadZone = 5.0;
        private const double KeyRotationDelta = 2.0;

        private const double VerticalAngleMin = -90.0;
        private const double VerticalAngleMax = 45.0;

        // ─────────────────────────────────────────────────────────────────────
        // NOUVELLES CONSTANTES — Autorotation
        // ─────────────────────────────────────────────────────────────────────
        // Durée d'un tour complet (360°) en secondes.
        // Changez ces valeurs pour accélérer ou ralentir chaque mode.
        private const double AutoRotateFastSeconds = 20.0;  // Tour rapide : 20 s
        private const double AutoRotateSlowSeconds = 60.0;  // Tour lent   : 60 s

        // ─────────────────────────────────────────────────────────────────────
        // NOUVELLES CONSTANTES — Opacité progressive de la barre de boutons
        // ─────────────────────────────────────────────────────────────────────
        // Opacité cible quand le panorama EST en mouvement (presque invisible).
        private const double OpacityMin = 0.15;
        // Opacité cible quand le panorama est au REPOS (pleinement visible).
        private const double OpacityFull = 1.0;
        // Délai d'inactivité (en secondes) avant de réafficher les boutons.
        private const double InactivityDelay = 0.5;
        // Vitesse du fondu (en unités d'opacité par seconde).
        // Plus la valeur est grande, plus la transition est rapide.
        private const double FadeSpeed = 2.5;

        // ─────────────────────────────────────────────────────────────────────
        // Champs 3D
        // ─────────────────────────────────────────────────────────────────────
        private PerspectiveCamera _camera = new PerspectiveCamera();
        private AxisAngleRotation3D _horizontalRotation = new AxisAngleRotation3D();
        private AxisAngleRotation3D _verticalRotation = new AxisAngleRotation3D();

        // ─────────────────────────────────────────────────────────────────────
        // Champs navigation souris
        // ─────────────────────────────────────────────────────────────────────
        private bool _isMouseDown = false;
        private double _startMouseX;
        private double _startMouseY;
        private double _lastMouseX;
        private double _lastMouseY;
        private TimeSpan _lastRenderTime;

        // ─────────────────────────────────────────────────────────────────────
        // Champs SpaceMouse 3Dconnexion
        // ─────────────────────────────────────────────────────────────────────
        private Device _smDevice;
        private Sensor _smSensor;
        private bool _spaceMouseEnabled = false;

        // ─────────────────────────────────────────────────────────────────────
        // Autorotation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Énumération des 3 états possibles pour l'autorotation.
        /// L'ordre définit le cycle : Off → Lent → Rapide → Off → …
        /// </summary>
        private enum AutoRotationState { Off, Lent, Rapide }

        // État courant de l'autorotation (commence arrêté).
        private AutoRotationState _autoRotState = AutoRotationState.Off;

        // ─────────────────────────────────────────────────────────────────────
        // Opacité progressive
        // ─────────────────────────────────────────────────────────────────────
        // Horodatage du dernier mouvement détecté (souris OU autorotation OU SpaceMouse).
        private DateTime _lastMovementTime = DateTime.MinValue;

        // Indique si un mouvement était actif au frame précédent.
        // Sert à détecter le passage repos ↔ mouvement sans heuristique trop lourde.
        private bool _isMoving = false;

        // ─────────────────────────────────────────────────────────────────────
        // Référence au contexte QuickLook et au chemin du fichier image
        // ─────────────────────────────────────────────────────────────────────
        private readonly QuickLook.Common.Plugin.ContextObject _context;
        private readonly string _imagePath;

        // ─────────────────────────────────────────────────────────────────────
        // Constructeur
        // ─────────────────────────────────────────────────────────────────────
        public Pano360Panel(QuickLook.Common.Plugin.ContextObject context, string imagePath)
        {
            InitializeComponent();
            _context = context;
            _imagePath = imagePath;

            // Initialisation de la scène 3D dès que le contrôle est chargé
            Loaded += (s, e) => InitScene();
            Unloaded += (s, e) => Dispose();

            // Clic droit → bascule plein écran (fonctionnalité Guillaume originale)
            MouseRightButtonUp += (s, e) => ToggleFullscreen();
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
            if (e.MiddleButton == MouseButtonState.Pressed) Window.GetWindow(this)?.Close();  // Clic molette → fermer le panneau (fonctionnalité perdue de l'assembly principal)

            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (_autoRotState != AutoRotationState.Off)
            {
                // Si l'autorotation est active, on la désactive au clic pour
                // donner le contrôle à l'utilisateur.
                _autoRotState = AutoRotationState.Off;
                btnAutoRotate.Content = "⏸ Off";
            }

            _isMouseDown = true;
            var pos = e.GetPosition(this);
            _lastMouseX = _startMouseX = pos.X;
            _lastMouseY = _startMouseY = pos.Y;
            Mouse.Capture(this);

            // Signale le début d'un mouvement (pour le fondu de la barre)
            MarquerMouvement();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isMouseDown = false;
            Mouse.Capture(null);
            // Pas besoin de changer l'opacité ici ; UpdateBarreOpacity() le fera
            // automatiquement après InactivityDelay secondes sans mouvement.
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
            
            // 2. On récupère et on lance l'animation XAML
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

            // ── Autorotation ───────────────────────────────────────────────
            // Si l'autorotation est active ET que l'utilisateur ne navigue pas
            // manuellement, on fait tourner la sphère en continu.
            if (_autoRotState != AutoRotationState.Off && !_isMouseDown)
            {
                // Calcule la vitesse angulaire en degrés/seconde :
                //   360° ÷ durée_d_un_tour
                double secondsPerTurn = (_autoRotState == AutoRotationState.Rapide)
                    ? AutoRotateFastSeconds
                    : AutoRotateSlowSeconds;

                double degreesPerSecond = 360.0 / secondsPerTurn;

                _horizontalRotation.Angle += degreesPerSecond * elapsed;

                // L'autorotation est un mouvement continu : on met à jour l'horodatage
                MarquerMouvement();
            }

            // ── Mise à jour de l'opacité de la barre de boutons ───────────
            UpdateBarreOpacity(elapsed);
        }

        // ─────────────────────────────────────────────────────────────────────
        // OPACITÉ PROGRESSIVE — cœur de la logique d'effacement/réapparition
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Appelée à chaque frame. Calcule si le panorama est en mouvement
        /// et ajuste progressivement l'opacité de la barre de boutons.
        ///
        /// Logique :
        ///   • Mouvement en cours  → fondu vers OpacityMin  (barre discrète)
        ///   • Repos depuis > InactivityDelay s → fondu vers OpacityFull (barre visible)
        ///
        /// Le fondu est proportionnel au temps écoulé (elapsed) * FadeSpeed,
        /// ce qui donne une transition fluide indépendante du taux de rafraîchissement.
        /// </summary>
        private void UpdateBarreOpacity(double elapsed)
        {
            double timeSinceLastMove = (DateTime.Now - _lastMovementTime).TotalSeconds;

            // Détermine si l'on est actuellement "en mouvement"
            bool movingNow = _isMouseDown
                          || _autoRotState != AutoRotationState.Off
                          || _spaceMouseEnabled;  // SpaceMouse peut bouger à tout moment

            double targetOpacity;

            if (movingNow || timeSinceLastMove < InactivityDelay)
            {
                // Mouvement actif (ou encore dans la fenêtre d'inactivité) → on cache
                targetOpacity = OpacityMin;
            }
            else
            {
                // Repos prolongé → on réaffiche
                targetOpacity = OpacityFull;
            }

            // Interpolation linéaire vers la cible (Lerp)
            // Math.Sign(…) donne la direction, Math.Min(…) évite de dépasser la cible
            double current = barreBtn.Opacity;
            double diff = targetOpacity - current;

            if (Math.Abs(diff) > 0.005)   // Seuil pour éviter les micro-oscillations
            {
                double step = FadeSpeed * elapsed;
                // On avance vers la cible sans la dépasser
                barreBtn.Opacity = current + Math.Sign(diff) * Math.Min(Math.Abs(diff), step);
            }
            else
            {
                barreBtn.Opacity = targetOpacity;
            }
        }

        /// <summary>
        /// Méthode utilitaire : note l'heure courante comme "dernier mouvement détecté".
        /// À appeler dès qu'une action de navigation est détectée.
        /// </summary>
        private void MarquerMouvement()
        {
            _lastMovementTime = DateTime.Now;
        }

        // ─────────────────────────────────────────────────────────────────────
        // BOUTON AUTOROTATION — cycle entre les 3 états
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Gestionnaire du clic sur le bouton "AutoRotate".
        /// À chaque clic, l'état avance dans le cycle :
        ///   Off  →  Lent  →  Rapide  →  Off  → …
        /// Le libellé du bouton est mis à jour pour refléter l'état courant.
        /// </summary>
        private void BtnAutoRotate_Click(object sender, RoutedEventArgs e)
        {
            // Passage à l'état suivant dans le cycle
            switch (_autoRotState)
            {
                case AutoRotationState.Off:
                    _autoRotState = AutoRotationState.Lent;
                    btnAutoRotate.Content = "🐢 Lent";
                    break;

                case AutoRotationState.Rapide:
                    _autoRotState = AutoRotationState.Off;
                    btnAutoRotate.Content = "⏸ Off";
                    break;

                case AutoRotationState.Lent:
                    _autoRotState = AutoRotationState.Rapide;
                    btnAutoRotate.Content = "⏩ Rapide";
                    break;
            }

            // Si on vient de désactiver l'autorotation, on note que le mouvement
            // vient de s'arrêter pour déclencher la temporisation de réapparition.
            if (_autoRotState == AutoRotationState.Off)
            {
                _lastMovementTime = DateTime.Now;
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
        // Bascule plein écran (Guillaume)
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
