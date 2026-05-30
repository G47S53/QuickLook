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
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

using TDxInput;

using Media3D = System.Windows.Media.Media3D;

namespace QuickLook.Plugin.ImageViewer.Pano360
{
    public partial class Pano360Panel : UserControl, IDisposable
    {
        // -------------------------------------------------------------------------
        // Constantes
        // -------------------------------------------------------------------------

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

        // -------------------------------------------------------------------------
        // Champs 3D
        // -------------------------------------------------------------------------

        private PerspectiveCamera _camera = new PerspectiveCamera();
        private AxisAngleRotation3D _horizontalRotation = new AxisAngleRotation3D();
        private AxisAngleRotation3D _verticalRotation = new AxisAngleRotation3D();

        // -------------------------------------------------------------------------
        // Champs navigation souris
        // -------------------------------------------------------------------------

        private bool _isMouseDown = false;
        private double _startMouseX;
        private double _startMouseY;
        private double _lastMouseX;
        private double _lastMouseY;
        private TimeSpan _lastRenderTime;

        // -------------------------------------------------------------------------
        // Champs SpaceMouse 3Dconnexion
        // -------------------------------------------------------------------------

        private Device _smDevice;
        private Sensor _smSensor;
        private bool _spaceMouseEnabled = false;

        // -------------------------------------------------------------------------
        // Référence au context pour le debug via la barre de titre
        // -------------------------------------------------------------------------

        private readonly QuickLook.Common.Plugin.ContextObject _context;
        private readonly string _imagePath;

        // -------------------------------------------------------------------------
        // Constructeur
        // -------------------------------------------------------------------------

        public Pano360Panel(QuickLook.Common.Plugin.ContextObject context, string imagePath)
        {
            InitializeComponent();

            _context = context;
            _imagePath = imagePath;

            // Même approche que l'application standalone originale :
            // tout est synchrone dans Loaded, exactement comme SetupScene()
            // était appelé dans MenuOuvrir_Click.
            Loaded += (s, e) => InitScene();
            Unloaded += (s, e) => Dispose();
        }

        // -------------------------------------------------------------------------
        // Initialisation de la scène — synchrone, identique au code original
        // -------------------------------------------------------------------------

        private void InitScene()
        {
            var fileName = System.IO.Path.GetFileName(_imagePath);

            try
            {
                SetTitle(fileName, "DBG: SetupCamera...");
                SetupCamera();

                SetTitle(fileName, "DBG: SetupLight...");
                SetupLight();

                SetTitle(fileName, "DBG: CreateMaterial...");
                var material = CreatePanoramaMaterial(_imagePath);

                SetTitle(fileName, "DBG: SetupSphere...");
                SetupSphere(material);

                SetTitle(fileName, "DBG: SetupEventHandlers...");
                SetupEventHandlers();
                txtLoading.Visibility = Visibility.Collapsed;

                // ← LA LIGNE CLÉ : signale à QuickLook que le chargement est terminé
                // Sans elle, QuickLook maintient son spinner par-dessus tout le contenu.
                _context.IsBusy = false;

                Focus();

                // Titre final propre
                SetTitle(fileName, null);
            }
            catch (Exception ex)
            {
                SetTitle(fileName, "ERREUR : " + ex.Message);
            }
        }

        private void SetTitle(string fileName, string debugInfo)
        {
            _context.Title = debugInfo != null
                ? string.Format("[{0}] 360°: {1}", debugInfo, fileName)
                : string.Format("360°: {0}", fileName);
        }

        // -------------------------------------------------------------------------
        // Mise en place de la scène 3D
        // -------------------------------------------------------------------------

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

            _horizontalRotation = new AxisAngleRotation3D(new Media3D.Vector3D(0, 1, 0), 0);
            _verticalRotation = new AxisAngleRotation3D(new Media3D.Vector3D(1, 0, 0), 0);

            var transformGroup = new Transform3DGroup();
            transformGroup.Children.Add(new RotateTransform3D(_horizontalRotation));
            transformGroup.Children.Add(new RotateTransform3D(_verticalRotation));
            model.Transform = transformGroup;

            viewport3D.Children.Add(new ModelVisual3D { Content = model });
        }

        // -------------------------------------------------------------------------
        // Gestion des événements
        // -------------------------------------------------------------------------

        private void SetupEventHandlers()
        {
            CompositionTarget.Rendering += OnRendering;
            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
            MouseMove += OnMouseMove;
            MouseWheel += OnMouseWheel;
            KeyDown += OnKeyDown;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            _isMouseDown = true;
            var pos = e.GetPosition(this);
            _lastMouseX = _startMouseX = pos.X;
            _lastMouseY = _startMouseY = pos.Y;
            Mouse.Capture(this);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isMouseDown = false;
            Mouse.Capture(null);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMouseDown) return;
            var pos = e.GetPosition(this);
            _lastMouseX = pos.X;
            _lastMouseY = pos.Y;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0 ? -FovZoomStep : FovZoomStep;
            double newFov = Clamp(_camera.FieldOfView + delta, FovMin, FovMax);
            _camera.FieldOfView = newFov;
            txtFov.Text = string.Format("FOV: {0:F0}°", newFov);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    _horizontalRotation.Angle += KeyRotationDelta;
                    break;
                case Key.Right:
                    _horizontalRotation.Angle -= KeyRotationDelta;
                    break;
                case Key.Up:
                    ClampVertical(_verticalRotation.Angle + KeyRotationDelta);
                    break;
                case Key.Down:
                    ClampVertical(_verticalRotation.Angle - KeyRotationDelta);
                    break;
                case Key.Add:
                case Key.OemPlus:
                    OnMouseWheel(this, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120));
                    break;
                case Key.Subtract:
                case Key.OemMinus:
                    OnMouseWheel(this, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120));
                    break;
            }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isMouseDown) return;

            var args = (RenderingEventArgs)e;
            double elapsed = (args.RenderingTime - _lastRenderTime).TotalSeconds;
            _lastRenderTime = args.RenderingTime;

            if (elapsed > 0.1) return;

            double deltaX = _lastMouseX - _startMouseX;
            double deltaY = _lastMouseY - _startMouseY;

            double speedX = Math.Abs(deltaX) < MouseDeadZone ? 0 : deltaX * MouseSensitivity * elapsed;
            double speedY = Math.Abs(deltaY) < MouseDeadZone ? 0 : deltaY * MouseSensitivity * elapsed;

            _horizontalRotation.Angle += speedX;
            ClampVertical(_verticalRotation.Angle - speedY);
        }

        // -------------------------------------------------------------------------
        // SpaceMouse 3Dconnexion
        // -------------------------------------------------------------------------

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
            });
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private void ClampVertical(double newAngle)
        {
            _verticalRotation.Angle = Clamp(newAngle, VerticalAngleMin, VerticalAngleMax);
        }

        private static MeshGeometry3D CreatePanoramaSphere()
        {
            var mesh = new MeshGeometry3D();

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

        /// <summary>
        /// Identique au code original de l'application standalone :
        /// UriSource + CacheOption.OnLoad, pas de MemoryStream, pas de Freeze.
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

        // -------------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------------

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            DeconnecterSpaceMouse();
            viewport3D.Children.Clear();
        }
    }
}
