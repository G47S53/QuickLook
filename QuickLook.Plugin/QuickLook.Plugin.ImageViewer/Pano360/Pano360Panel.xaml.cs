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

using FellowOakDicom.Imaging.Reconstruction;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using TDxInput;
using Media3D = System.Windows.Media.Media3D;

using System.Runtime.InteropServices;          // Pour le DllImport            (Problème de la spacemouse qui perd le focus avec webview2)
using System.Windows.Interop;
using System.Runtime.CompilerServices;         // Pour le WindowInteropHelper  (Problème de la spacemouse qui perd le focus avec webview2)

namespace QuickLook.Plugin.ImageViewer.Pano360
{
    public partial class Pano360Panel : UserControl, IDisposable
    {
        #region Constantes et Déclarations de champs

        // ─────────────────────────────────────────────────────────────────────
        // > Référence au contexte QuickLook et au chemin du fichier image
        // ─────────────────────────────────────────────────────────────────────
        private readonly QuickLook.Common.Plugin.ContextObject _context;   // Objet QuickLook : title, IsBusy, BlocageShowCaption...
        private readonly string _imagePath;                                // Chemin du fichier panorama passé à l'ouverture du plugin (ne change jamais)

        // ─────────────────────────────────────────────────────────────────────
        // > Navigation entre panoramas du dossier courant
        // ─────────────────────────────────────────────────────────────────────
        private string _currentPanoPath;                                   // Chemin du panorama actuellement affiché (mis à jour à chaque navigation ◀/▶)
        private bool _isNavigating = false;                                // Verrou pour éviter un double déclenchement de navigation (clic rapide, touche + bouton, etc.)

        // ─────────────────────────────────────────────────────────────────────
        // > Préchargement (Cache)
        // ─────────────────────────────────────────────────────────────────────
        private readonly Dictionary<string, BitmapImage> _imageCache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase); // Cache RAM des BitmapImage déjà décodées, indexées par chemin fichier
        private bool _isPreloading = false;                                // Verrou : empêche deux passes de préchargement simultanées
        private System.Windows.Threading.DispatcherTimer _idleTimer;       // Timer déclenché toutes les 500 ms pour lancer le préchargement quand l'utilisateur ne fait rien
        private int _preloadHorizon = 2;                                   // Nombre de voisins préchargés de chaque côté (2 → 5 images en RAM : -2, -1, courant, +1, +2)

        // ─────────────────────────────────────────────────────────────────────
        // > Extraction données Exif/Xmp
        // ─────────────────────────────────────────────────────────────────────
        private PanoMetadata _currentMetadata;
        private bool _isMetaChanging = false;                              // Indique si les données Exif/Xmp ont changé et si il faut les sauvegarder
        private bool _isPanelVisible = false;                              // Indique si le panneau d'information est considéré comme visible (important pour enlever le panneau avec le titre si on change de panorama)

        // ─────────────────────────────────────────────────────────────────────
        // > Panneau Carte GPS (WebView2 + Leaflet/OSM)
        // ─────────────────────────────────────────────────────────────────────
        private bool _isMapPanelVisible = false;                           // Indique si le panneau carte est considéré comme visible
        private bool _isMapWebViewReady = false;                           // True une fois le CoreWebView2 initialisé et la carte Leaflet chargée
        private double? _pendingMapLat;                                    // Coordonnées en attente si on demande l'affichage avant que le WebView2 soit prêt
        private double? _pendingMapLon;

        private const double LargeurMinMap = 370;
        private const double HauteurMinMap = 280;

        private const double HauteurPanneauPositionInfo = 40;

        private class NominatimAddress
        {
            [JsonProperty("city")]
            public string City { get; set; }
            [JsonProperty("town")]
            public string Town { get; set; }
            [JsonProperty("village")]
            public string Village { get; set; }
            [JsonProperty("municipality")]
            public string Municipality { get; set; }
            [JsonProperty("country")]
            public string Country { get; set; }
            [JsonProperty("state")]
            public string State { get; set; }
            [JsonProperty("county")]
            public string Comte { get; set; }
            [JsonProperty("house_number")]
            public string HouseNumber { get; set; }
            [JsonProperty("road")]
            public string Road { get; set; }
            [JsonProperty("postcode")]
            public string PostCode { get; set; }
            [JsonProperty("country_code")]
            public string CountryCode { get; set; }
        }

        private static readonly HttpClient _httpClientGeocodage = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private CancellationTokenSource _ctsGeocodage;
        private string _dernierePositionGeocodee;
        private double _largeurPanneauPositionInfo = 220;                  // Largeur réelle du Border pnlPositionInfo, recalculée via FormattedText à chaque changement de texte (évite tout Measure()/layout pass pendant le drag)

        private double _dernierClicCarteLat;                               // Correspond à la position du clic sur la carte Leaflet
        private double _dernierClicCarteLon;
        private int _dernierClicZoom = 10;
        private bool _isContextMenu = false;
        private int _zoomPinMap;

        private bool _sliderHeadingMisAJourProgrammatique = false;         // Évite que la mise à jour programmatique du slider (au chargement d'un panorama) ne
                                                                           // déclenche elle-même ValueChanged et n'écrive _isMetaChanging = true à tort, alors que
                                                                           // l'utilisateur n'a rien modifié.
        
        private const double OffsetHeadingSphereVersGPano = -90.0;         // Décalage entre la convention interne de la sphère (au chargement, la caméra à Angle=0°
                                                                           // regarde vers U≈0.75 de la texture équirectangulaire) et la convention GPano (PoseHeadingDegrees
                                                                           // se réfère au centre de l'image, U=0.5). Déterminé empiriquement par comparaison avec Pano2Vr.
        private string _GpsMemoire1Nom = "Vide";
        private string _GpsMemoire1Latitude = "0";
        private string _GpsMemoire1Longitude = "0";
        private int _GpsMemoire1Zoom = 10;
        private string _GpsMemoire2Nom = "Vide";
        private string _GpsMemoire2Latitude = "0";
        private string _GpsMemoire2Longitude = "0";
        private int _GpsMemoire2Zoom = 10;
        private string _GpsMemoire3Nom = "Vide";
        private string _GpsMemoire3Latitude = "0";
        private string _GpsMemoire3Longitude = "0";
        private int _GpsMemoire3Zoom = 10;

        // ─────────────────────────────────────────────────────────────────────
        // > Sauvegarde des préférences (OPTIONS)
        // ─────────────────────────────────────────────────────────────────────
        // ─────── Attention : d'autres paramètres liés au tour unique se trouvent aussi ─────────────────
        // ─────── dans la section "Mode Tour Unique de démarrage avec accélération/décélération". ───────
        private readonly string _configPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickLook",
        "Pano360Settings.json");                                           // Chemin complet du fichier JSON de configuration (%APPDATA%\QuickLook\Pano360Settings.json)

        private bool _chkFullscreenSavedValue = false;                     // Option : démarrer en plein écran (lue depuis le JSON, appliquée au Loaded)
        private int _StartAutoRotate = 1;                                  // Option : vitesse de l'autorotation au démarrage (1=Lent, 2=Normal, 3=Rapide)
        private int _StartRadioBouton = 1;                                 // Option : mode de démarrage sélectionné (1=Statique, 2=AutoRotate, 3=1tour+Fermeture, 4=1tour+Suivant)
        private bool _StartOneTurnActive = false;                          // Option : activer "1 tour + fermeture" au démarrage (issu du JSON)
        private bool _StartOneTurnNext = false;                            // Option : activer "1 tour + panorama suivant" au démarrage (issu du JSON)
        private bool _oneTurnNextActive = false;                           // État d'exécution : true quand le mode "1 tour + suivant" est en cours de rotation
        private bool _autoNextFromButton = false;                          // True si le mode AutoNext a été déclenché par le bouton (et non par les options de démarrage) — sert à le relancer après navigation
        private AutoRotationState _autoNextSavedRotState = AutoRotationState.Normal; // Vitesse d'autorotation mémorisée au moment du clic "AutoNext bouton", pour la restaurer après navigation
        private double _OptionFov = 90.0;                                  // Option : FOV par défaut en degrés (lu depuis JSON, appliqué à la caméra au démarrage)
        private bool _OptionOpen = false;                                  // True quand le panneau Options est affiché (bloque clavier, SpaceMouse, autorotation)
        private bool _OptionBarreReduite = false;                          // Option : démarrer avec la barre de boutons en mode compact (sans les groupes gauche/droite)

        private Pano360Settings settings;                                  // Instance désérialisée du fichier JSON (utilisée par LoadSettings / SaveSettings)
        private List<string> _DerniersMotsCles = new List<string>();       // Les 6 derniers mots-clés utilisés via la boite de dialogue Titre/Mots-clés (le plus récent en premier), issu du JSON

        // ─────── Structure pour le stockage des options ───────
        public class Pano360Settings
        {
            public double AutoRotateSlowSeconds { get; set; } = 60.0;                // Durée d'un tour complet en mode Lent (secondes)
            public double AutoRotateNormalSeconds { get; set; } = 20.0;              // Durée d'un tour complet en mode Normal (secondes)
            public double AutoRotateFastSeconds { get; set; } = 10.0;                // Durée d'un tour complet en mode Rapide (secondes)
            public bool FullscreenStartup { get; set; } = false;                     // Ouvre la fenêtre en plein écran au démarrage
            public bool StartBarreReduite { get; set; } = false;                     // Démarre avec la barre de boutons compactée (uniquement FOV + ◀▶)
            public double DefaultFov { get; set; } = 90.0;                           // Champ de vision par défaut (degrés), appliqué à la caméra à l'ouverture
            public int StartRadioBouton { get; set; } = 1;                           // Mode de démarrage (voir _StartRadioBouton)
            public int StartAutoRotate { get; set; } = 1;                            // Vitesse d'autorotation au démarrage (voir _StartAutoRotate)
            public bool StartOneTurnAndNext { get; set; } = false;                   // Démarrer directement en mode "1 tour + panorama suivant"
            public bool StartOneTurnAndClose { get; set; } = false;                  // Démarrer directement en mode "1 tour + fermeture"
            public double StartOneTurnDuration { get; set; } = 15.0;                 // Durée totale du tour unique en secondes (profil trapézoïdal accel/croisière/decel)
            public string GpsMemoire1Nom { get; set; } = "Vide";                     // Nom de la première mémoire GPS
            public string GpsMemoire1Latitude { get; set; } = "0";                   // Latitude de la première mémoire GPS
            public string GpsMemoire1Longitude { get; set; } = "0";                  // Longitude de la première mémoire GPS
            public int GpsMemoire1Zoom { get; set; } = 10;
            public string GpsMemoire2Nom { get; set; } = "Vide";                     // Nom de la seconde mémoire GPS
            public string GpsMemoire2Latitude { get; set; } = "0";                   // Latitude de la seconde mémoire GPS
            public string GpsMemoire2Longitude { get; set; } = "0";                  // Longitude de la seconde mémoire GPS
            public int GpsMemoire2Zoom { get; set; } = 10;
            public string GpsMemoire3Nom { get; set; } = "Vide";                     // Nom de la troisième mémoire GPS
            public string GpsMemoire3Latitude { get; set; } = "0";                   // Latitude de la troisième mémoire GPS
            public string GpsMemoire3Longitude { get; set; } = "0";                  // Longitude de la troisième mémoire GPS
            public int GpsMemoire3Zoom { get; set; } = 10;
            public List<string> DerniersMotsCles { get; set; } = new List<string>(); // Les 6 derniers mots-clés utilisés via la boite de dialogue Titre/Mots-clés (le plus récent en premier)
        }

        // ─────────────────────────────────────────────────────────────────────
        // > Constantes — paramètres de la sphère et de la navigation
        // ─────────────────────────────────────────────────────────────────────
        private const int SphereSlices = 72;             // Nombre de segments horizontaux du mesh sphérique (plus = plus lisse, plus coûteux)
        private const int SphereStacks = 36;             // Nombre de segments verticaux du mesh sphérique

        private const double FovMin = 30.0;              // FOV minimum autorisé en degrés (zoom maximum — vision très zoomée)
        private const double FovMax = 150.0;             // FOV maximum autorisé en degrés (zoom minimum — grand angle)
        private const double FovDefault = 90.0;          // FOV utilisé à l'initialisation de la caméra (avant lecture des settings)
        private const double FovZoomStep = 5.0;          // Pas de zoom fixe (non utilisé directement — remplacé par le pas adaptatif de la molette)

        private const double MouseSensitivity = 1.0;     // Coefficient multiplicateur appliqué au déplacement souris → vitesse de rotation
        private const double MouseDeadZone = 5.0;        // Zone morte en pixels : déplacements inférieurs à cette valeur sont ignorés (évite les micro-tremblements)

        private const double VerticalAngleMin = -80.0;   // Angle vertical minimum (regarde vers le bas, axe X) — en degrés
        private const double VerticalAngleMax = 70.0;    // Angle vertical maximum (regarde vers le haut, axe X) — en degrés

        private const double SphereVPower = 1.1;         // Déformation UV verticale : 1.0 = aucune (standard équirectangulaire)
                                                         // < 1.0 → étirement vers les pôles (effet "little planet")
                                                         // > 1.0 → compression vers les pôles (ciel/sol plus "plat")
                                                         // Plage utile : 0.5 à 2.0

        // ─────────────────────────────────────────────────────────────────────
        // > Champs 3D
        // ─────────────────────────────────────────────────────────────────────
        private PerspectiveCamera _camera = new PerspectiveCamera();                     // Caméra WPF positionnée au centre de la sphère (position fixe 0,0,0)
        private AxisAngleRotation3D _horizontalRotation = new AxisAngleRotation3D();     // Rotation autour de l'axe Y (panoramique horizontal, lacet / yaw)
        private AxisAngleRotation3D _verticalRotation = new AxisAngleRotation3D();       // Rotation autour de l'axe X (inclinaison verticale, tangage / pitch)

        private double _homeHorizontalAngle = 0.0;           // Angle horizontal mémorisé au chargement de la scène (vue "Home", touche Fit de la SpaceMouse)
        private double _homeVerticalAngle = 0.0;             // Angle vertical mémorisé au chargement de la scène (vue "Home")
        private double _homeFov = 0.0;

        // ─────────────────────────────────────────────────────────────────────
        // > Champs navigation souris
        // ─────────────────────────────────────────────────────────────────────
        private bool _isMouseDown = false;               // True tant que le bouton gauche est enfoncé (mode drag en cours)
        private double _startMouseX;                     // Position X de la souris au moment du MouseDown (référence pour calculer le delta)
        private double _startMouseY;                     // Position Y de la souris au moment du MouseDown (référence pour calculer le delta)
        private double _lastMouseX;                      // Dernière position X connue de la souris (mise à jour à chaque MouseMove)
        private double _lastMouseY;                      // Dernière position Y connue de la souris (mise à jour à chaque MouseMove)
        private TimeSpan _lastRenderTime;                // Horodatage du dernier frame rendu (pour calculer le delta temps entre deux frames)
        private TimeSpan _lastFovUpdateTime;             // Horodatage de la dernière mise à jour du triangle FOV sur la carte (throttle indépendant de _lastRenderTime)
        private bool _isMouseInertia = false;            // True quand la vitesse courante est due à un "lancer" de souris (applique le frein aérodynamique quadratique)
        private bool _isPopupShown = false;              // Verrou : empêche le popup "Fermeture automatique" de se déclencher plusieurs fois sur le même tour (AutoClose classique)
        private bool _isPopupShownPour1Tour = false;     // Verrou : empêche le popup de fin de tour de s'afficher plusieurs fois (mode OneTurn + fermeture)
        private bool _isPopupShownPour1TourNum2 = false; // Verrou : même chose pour le popup "Chargement suivant..." (mode OneTurn + panorama suivant)
        private double _targetFov = FovDefault;          // FOV cible vers lequel la caméra converge en douceur (interpolation exponentielle dans OnRendering)
        private bool _isFovAnimating = false;            // True pendant qu'une animation FOV est en cours (évite de relancer le storyboard à chaque frame)

        // ─────────────────────────────────────────────────────────────────────
        // > Champs SpaceMouse 3Dconnexion
        // ─────────────────────────────────────────────────────────────────────
        private Device _smDevice;                        // Périphérique COM TDxInput (objet racine de la SpaceMouse)
        private Sensor _smSensor;                        // Capteur 6 DOF de la SpaceMouse (rotations + translations)
        private TDxInput.Keyboard _keyboardSpaceMouse;   // Clavier de la SpaceMouse (boutons latéraux programmables)

        private double sensibiliteTangage = 0.05;        // Coefficient de sensibilité pour l'axe X (tangage / pitch)
        private double sensibiliteLacet = 0.05;          // Coefficient de sensibilité pour l'axe Y (lacet / yaw — rotation horizontale du panorama)
        //private double sensibiliteRoulis = 0.002;       // [Non utilisé] Coefficient pour l'axe Z (roulis / roll — non implémenté)

        private bool _spaceMouseEnabled = false;         // True quand la SpaceMouse est connectée et active (évite une double connexion)

        private readonly object _spaceMouseLock = new object(); // Verrou thread-safe : les valeurs brutes sont écrites depuis le thread COM de la SpaceMouse et lues depuis le thread UI
        private double _rawSpaceMouseX = 0;              // Valeur brute de l'axe X de rotation (tangage), mise à jour par l'événement COM SensorInput
        private double _rawSpaceMouseY = 0;              // Valeur brute de l'axe Y de rotation (lacet), mise à jour par l'événement COM SensorInput
        private double _rawSpaceMouseZ = 0;              // Valeur brute de l'axe Z de rotation (roulis, non utilisé en pratique)
        private double _rawSpaceMouseZoom = 0;           // Valeur brute de Translation.Z (avant/arrière du manche), utilisée pour le zoom FOV

        private const double ZoomDeadZone = 50.0;        // Zone morte du zoom SpaceMouse : valeurs Translation.Z en-dessous de ce seuil sont ignorées (évite le zoom involontaire lors des rotations)
        private const double ZoomSensitivity = 0.05;     // Coefficient multiplicateur du zoom SpaceMouse (valeur faible = zoom progressif et précis)
        private const double ZoomFreeSpeedXY = 1000;     // Seuil de vitesse XY au-dessus duquel le zoom est inhibé (si on tourne vite, on ne zoome pas accidentellement)

        private bool _isSpaceMouseZoom = false;          // Verrou mis à true si le zoom via la spacemouse est actif, dans ce cas cela désactive la roulette
        private bool _isSpaceMouseHighForce = false;     // Utilisé pour avertir que l'on utilise en même temps la souris et la spacemouse 

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        // ─────────────────────────────────────────────────────────────────────
        // > Raccourcis Clavier SpaceMouse — navigation vers angle cible
        // ─────────────────────────────────────────────────────────────────────
        private double _targetHorizontalAngle = double.NaN;  // Angle horizontal cible pour le snap clavier (NaN = aucun snap actif, la sphère tourne librement)
        private double _targetVerticalAngle = double.NaN;    // Angle vertical cible pour le snap clavier (NaN = aucun snap actif)
        private const double KeySnapSpeed = 180.0;           // Vitesse maximale de rotation animée lors d'un snap clavier (°/s) — interpolation ease-out

        // ─────────────────────────────────────────────────────────────────────
        // > Autorotation
        // ─────────────────────────────────────────────────────────────────────
        private enum AutoRotationState { Off, Lent, Normal, Rapide } // Quatre états possibles de la rotation automatique (Off = arrêtée)

        private AutoRotationState _autoRotState = AutoRotationState.Off; // État courant de l'autorotation — démarre toujours à Off

        // ─────── Durées d'un tour complet (360°) par mode. Modifiables via le panneau Options. ───────
        // ─────── Ces valeurs sont écrasées par les settings JSON au chargement. ──────────────────────
        private double AutoRotateFastSeconds = 10.0;         // Durée du tour en mode Rapide (secondes) — valeur par défaut
        private double AutoRotateNormalSeconds = 20.0;       // Durée du tour en mode Normal (secondes) — valeur par défaut
        private double AutoRotateSlowSeconds = 60.0;         // Durée du tour en mode Lent (secondes) — valeur par défaut

        // ─────────────────────────────────────────────────────────────────────
        // > AutoClose - Moteur Physique "La Jamais Contente" 🏎️ (Accélération / Décélération)
        // ─────────────────────────────────────────────────────────────────────
        private double _currentRotationSpeed = 0.0;          // Vitesse angulaire instantanée appliquée à la sphère (°/s) — peut être négative (inertie vers la gauche)
        private const double AccelerationRate = 18.0;        // Taux d'accélération du moteur physique (°/s²) — montée en vitesse lors du démarrage de l'autorotation
        private const double DecelerationRate = 45.0;        // Taux de décélération de base (°/s²) — frein appliqué à l'arrêt et à la fin du tour AutoClose

        private bool _autoCloseActive = false;               // True quand le mode "1 tour + fermeture/suivant" est actif (moteur de suivi d'angle en route)
        private double _autoCloseTargetAngle = -1;           // Angle horizontal de destination pour fermer/passer au suivant (-1 = non défini) — égal à _autoCloseStartAngle (tour complet)
        private double _autoCloseStartAngle = -1;            // Angle horizontal au moment du déclenchement AutoClose (-1 = non défini) — sert à calculer l'arc parcouru
        private bool _hasLeftStartZone = false;              // Devient True quand la sphère a quitté la zone de départ (±6°) — sécurité contre la fermeture immédiate au déclenchement
        private bool _isAutoClosingPhase = false;            // True uniquement pendant la phase de freinage final (après le tour complet, avant l'arrêt)

        // ─────────────────────────────────────────────────────────────────────
        // > Mode Tour Unique de démarrage avec accélération/décélération
        // ─────────────────────────────────────────────────────────────────────
        private bool _oneTurnActive = false;                 // True pendant l'exécution d'un tour unique (profil trapézoïdal géré dans OnRendering)
        private double _oneTurnTimer = 0.0;                  // Chronomètre interne du tour unique (secondes écoulées depuis le démarrage)
        private double _oneTurnDuration = 15.0;              // Durée totale imposée du tour unique (secondes) — récupérée depuis les settings
        private double _oneTurnStartAngle = 0.0;             // Angle horizontal au démarrage du tour unique (pour le recalage parfait à 360° à la fin)
        private double _oneTurnAccelTime = 2.5;              // Durée des phases d'accélération et de décélération (secondes) — profil symétrique trapézoïdal
        private double _oneTurnVMax = 0.0;                   // Vitesse de croisière calculée pour tenir le tour en _oneTurnDuration (°/s) — calculée au démarrage
        private double _oneTurnAccelRate = 0.0;              // Taux d'accélération/décélération calculé pour atteindre _oneTurnVMax en _oneTurnAccelTime (°/s²)

        // ─────────────────────────────────────────────────────────────────────
        // > Barre de boutons
        // ─────────────────────────────────────────────────────────────────────
        private const double InactivityDelay = 0.5;           // Délai d'inactivité (secondes) après lequel la barre repasse en opaque si la scène s'est arrêtée
        private bool _isBarreCompactee = false;               // True quand la barre est en mode compact (double-clic sur le FOV) — les groupes gauche/droite sont masqués

        // ─────────────────────────────────────────────────────────────────────
        // > Opacité progressive de la barre de boutons
        // ─────────────────────────────────────────────────────────────────────
        private DateTime _lastMovementTime = DateTime.MinValue; // Horodatage du dernier mouvement détecté (souris, autorotation ou SpaceMouse) — sert à déclencher le fondu de la barre

        // ─────── Indique si un mouvement était actif au frame précédent. ────────────────────────────
        // ─────── Sert à détecter le passage repos ↔ mouvement sans heuristique trop lourde. ─────────

        private bool _isBarreMasquee = false;                   // True quand la barre est actuellement en opacité réduite (animation FadeOut appliquée) — évite de relancer le storyboard à chaque frame
        private bool _isMouseOverBarre = false;                 // True quand le curseur survole la barre ou le panneau de progression — force la barre à rester visible

        #endregion Constantes et Déclarations de champs
        // ─────────────────────────────────────────────────────────────────────
        // Constructeur
        // ─────────────────────────────────────────────────────────────────────
        public Pano360Panel(QuickLook.Common.Plugin.ContextObject context, string imagePath)
        {
            // ── -1. Forcer TLS 1.2 pour les appels HTTP sortants (ex: géocodage Nominatim) ──
            // Sur .NET Framework, le protocole TLS par défaut négocié par ServicePointManager peut être
            // trop ancien (SSL3/TLS1.0/1.1) selon la config système, ce que des serveurs comme Nominatim
            // refusent silencieusement (l'erreur remontée côté client est un HttpRequestException générique,
            // sans détail). On force explicitement TLS 1.2 (et 1.3 si dispo) avant tout appel HttpClient.
            try
            {
                // Tls13 n'existe pas dans toutes les versions du framework cible : on retombe sur Tls12 seul.
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            }
            catch
            {

            }

            // ── 0. Optimisation système ──
            try
            {
                using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                currentProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            }
            catch { }

            // ── 1. Construction de l'interface XAML (Obligatoire en premier) ──
            InitializeComponent();

            // ── 2. Variables de base ──
            _context = context;
            _imagePath = imagePath;
            _currentPanoPath = imagePath;

            // ── 3. Paramètres et état initial ──
            Mouse.OverrideCursor = null;
            MajEtatAutoCloseAutoNext();
            LoadSettings();

            // ── 4. Abonnements aux événements différés (Au moment de l'affichage) ──
            Loaded += (s, e) =>
            {
                // Initialisation de la scène
                InitScene();

                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.PreviewKeyDown += OnWindowKeyDown;
                    window.PreviewKeyUp += OnWindowKeyUp;
                }

                // Démarrage du chronomètre de préchargement une fois que tout est prêt
                _idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _idleTimer.Tick += OnIdleTimerTick;
                _idleTimer.Start();
            };

            Unloaded += (s, e) => Dispose();
            MouseRightButtonUp += (s, e) => ToggleFullscreen();
            this.SizeChanged += Pano360Panel_SizeChanged;
        }
        #region Initialisation & Moteur 3D de rendu
        // ─────────────────────────────────────────────────────────────────────
        // Scène 3D
        // ─────────────────────────────────────────────────────────────────────
        private void InitScene()
        {
            // lire les données Exif/Xmp de la photo
            LoadAndDisplayMetadata(_currentPanoPath);
            _isMetaChanging = false;                         // initialise l'état de l'indicateur de sauvegarde

            // Initialisation de la scène 3D
            SetupCamera();
            SetupLight();
            var material = CreatePanoramaMaterial(_imagePath);
            SetupSphere(material);
            SetupEventHandlers();

            _context.IsBusy = false;  // Signale à QuickLook que le chargement est terminé

            Focus();

            // Debug pour les données Exif/Xmp
            //PanoMetadataService.DiagnostiquerMetadonnees(_currentPanoPath);

            // ── Connexion SpaceMouse une fois la scène complètement prête ──
            ConnecterSpaceMouse();
            btnSpaceMouse.Checked -= BtnSpaceMouse_Checked;
            btnSpaceMouse.IsChecked = true; // Reflète l'état visuel du bouton uniquement
            btnSpaceMouse.Checked += BtnSpaceMouse_Checked;
        }
        private void SetupCamera()
        {
            // Preparation caméra
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

            // Mise à jour avec la position Home (vide ou enregistrée)
            _horizontalRotation.Angle = _homeHorizontalAngle;
            _verticalRotation.Angle = _homeVerticalAngle;

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
                    mesh.TextureCoordinates.Add(new Point(slice / (double)SphereSlices, Math.Pow(phi / Math.PI, SphereVPower)));
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
        private Material CreatePanoramaMaterial(string imagePath)
        {
            ImageSource imageSource;

            if (_imageCache.TryGetValue(imagePath, out var cachedImg))
            {
                // Cache Hit : le fichier a été préchargé en fond !
                imageSource = cachedImg;
                System.Diagnostics.Debug.WriteLine($"[Cache] HIT pour : {System.IO.Path.GetFileName(imagePath)}");
            }
            else
            {
                // Cache Miss : Chargement classique de secours
                var image = new BitmapImage();
                try
                {
                    image.BeginInit();
                    image.UriSource = new Uri(imagePath, UriKind.Absolute);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    imageSource = image;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Erreur chargement image 360° : " + ex.Message);
                    return new DiffuseMaterial(new SolidColorBrush(Colors.DimGray));
                }
            }

            var brush = new ImageBrush(imageSource)
            {
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                TileMode = TileMode.FlipX,
                Stretch = Stretch.Fill
            };

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

            // ──────────────────────────────────────────────────────────────────────
            // ─ -1. Mise à jour du triangle FOV sur la carte GPS (throttle ~10x/s, suffisant visuellement pour le WebView2  ──
            if ((args.RenderingTime - _lastFovUpdateTime).TotalSeconds > 0.05)
            {
                _lastFovUpdateTime = args.RenderingTime;
                _ = MettreAJourFovSurCarte();
            }

            // ──────────────────────────────────────────────────────────────────────
            // ── 0. TEST : Si l'écran est trop rapide (ex: 144Hz), on ignore la frame 
            // pour forcer un rythme de 60 FPS maximum (1 frame toutes les ~16ms) ---
            if (elapsed < 0.016) return;
            // Protection contre les délais aberrants (ex. : fenêtre minimisée)
            if (elapsed > 0.1) return;

            // ──────────────────────────────────────────────────────────────────────
            // ── 1. NAVIGATION SOURIS (Capture de la vitesse pour l'inertie) ───────
            if (_isMouseDown)
            {
                //Gestion des abus d'outils
                if (_isSpaceMouseHighForce)
                {
                    txtInfoPopup.Text = "La souris l'emporte, arrête de jouer. 😉";
                    (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                }

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

            //Pour le flag de la souris, savoir si il y a utilisation des 2 periphériques (Gestion des abus d'outils)
            _isSpaceMouseHighForce = (Math.Abs(spaceMouseSpeedX) > 500 || Math.Abs(spaceMouseSpeedY) > 500 || Math.Abs(spaceMouseSpeedZ) > 500);

            if (!_isMouseDown)
            {
                // Partie rotation du panorama
                if (spaceMouseSpeedX != 0 || spaceMouseSpeedY != 0 || spaceMouseSpeedZ != 0)
                {
                    //Gestion des abus d'outils
                    if (Math.Abs(spaceMouseSpeedY) > 500 && (_autoRotState != AutoRotationState.Off || _oneTurnActive))
                    {
                        txtInfoPopup.Text = "🕹️ Axe horizontal vérouillé dans ce mode";
                        (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                    }

                    if (_autoRotState == AutoRotationState.Off && !_oneTurnActive)
                    {
                        _horizontalRotation.Angle -= spaceMouseSpeedY * elapsed * sensibiliteLacet;
                    }

                    ClampVertical(_verticalRotation.Angle + (spaceMouseSpeedX * elapsed * sensibiliteTangage));

                    MarquerMouvement();
                }

                // Partie Zoom via le déplacement du manche vers l'avant/arrière
                bool panoramaEnMouvement = Math.Abs(spaceMouseSpeedX) > ZoomFreeSpeedXY || Math.Abs(spaceMouseSpeedY) > ZoomFreeSpeedXY;

                if (!panoramaEnMouvement && Math.Abs(_rawSpaceMouseZoom) > ZoomDeadZone)
                {
                    _isSpaceMouseZoom = true;   // Pour la gestion des abus d'outils
                    _targetFov = Clamp(_targetFov + _rawSpaceMouseZoom * ZoomSensitivity * elapsed, FovMin, FovMax);
                }
                else
                {
                    _isSpaceMouseZoom = false;  // Pour la gestion des abus d'outils
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
                    txtInfoPopup.Text = "Arrêt rotation automatique";
                    (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                    _oneTurnActive = false;
                    _oneTurnNextActive = false; // ← AJOUT
                    _StartOneTurnNext = false;
                    _autoCloseActive = false;
                    btnAutoRotate.Content = "Rotation auto.";
                    btnAutoRotate.Background = Brushes.Transparent;
                    btnAutoRotate.IsEnabled = true;
                    _isPopupShown = false;
                    MajEtatAutoCloseAutoNext();
                }
                else
                {
                    // ── Gestion de 1 tour et on ferme ──
                    // ───────────────────────────────────
                    _oneTurnTimer += elapsed;

                    double tempsRestantPour1Tour = _oneTurnDuration - _oneTurnTimer;

                    // Mise à jour de l'anneau de progression (géré ici car _autoCloseActive = false en mode OneTurnNext)
                    double angleParcouru1Tour = (_horizontalRotation.Angle - _oneTurnStartAngle + 360.0) % 360.0;
                    rectProgressTransform.Angle = angleParcouru1Tour;
                    txtTempsRestant.Text = $"{tempsRestantPour1Tour:F0} s";

                    // Profil de vitesse trapézoïdal
                    if (_oneTurnTimer <= _oneTurnAccelTime)
                    {
                        // ── Phase 1 : Accélération
                        _currentRotationSpeed = _oneTurnAccelRate * _oneTurnTimer;
                    }
                    else if (_oneTurnTimer >= (_oneTurnDuration - _oneTurnAccelTime))
                    {
                        // ── Phase 3 : Décélération
                        if (tempsRestantPour1Tour < 0) tempsRestantPour1Tour = 0;
                        _currentRotationSpeed = _oneTurnAccelRate * tempsRestantPour1Tour;
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

                        // Nettoyage de l'état pour éviter que l'AutoClose ne se déclenche
                        _autoCloseActive = false;
                        _isAutoClosingPhase = false;
                        _autoCloseTargetAngle = -1;
                        _autoCloseStartAngle = -1;
                        _hasLeftStartZone = false;

                        if (_oneTurnNextActive)
                        {
                            _oneTurnNextActive = false;
                            System.Diagnostics.Debug.WriteLine("Pano suivant !");                     //Debug
                            Dispatcher.BeginInvoke(new Action(() => NavigateToAdjacentPano(+1)));
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("Fermeture de la fenetre !");          //Debug
                            Dispatcher.BeginInvoke(new Action(() => Window.GetWindow(this)?.Close()));
                        }
                        return;
                    }

                    if (tempsRestantPour1Tour <= 1.5 && !_isPopupShownPour1Tour && !_oneTurnNextActive)
                    {
                        _isPopupShownPour1Tour = true;
                        txtInfoPopup.Text = "Fermeture automatique...";

                        if (_oneTurnDuration <= 6)
                            (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                        else
                            (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                    }

                    if (tempsRestantPour1Tour <= 0.5 && !_isPopupShownPour1TourNum2 && _oneTurnNextActive)
                    {
                        _isPopupShownPour1TourNum2 = true;
                        //Affichage d'un message
                        txtInfoPopup.Text = "Chargement panorama suivant ...";
                        (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
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

                        // Formule physique : Frein linéaire de base + (Coefficient * Vitesse²), le coefficient 0.02 est notre "profil aérodynamique" à ajuster.
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
                // ── Compte à rebours : tourne tant qu'AutoClose est actif, phases confondues ──
                if (_autoCloseActive && !_oneTurnActive)
                {
                    double tempsRestant = ComputeAutoCloseTimeRemaining();
                    txtTempsRestant.Text = $"{tempsRestant:F0} s";
                }

                // ── Détection de progression et déclenchement popup (seulement hors phase de freinage) ──
                if (_autoCloseActive && _autoCloseTargetAngle >= 0 && !_isAutoClosingPhase)
                {
                    double angleActuel = _horizontalRotation.Angle;
                    double diffAngulaire = Math.Abs(angleActuel - _autoCloseStartAngle);

                    // 🎯 Icône de l'anneau : Calcul linéaire parfait du parcours de 0° à 360°
                    double angleParcouru = (angleActuel - _autoCloseStartAngle + 360) % 360;
                    rectProgressTransform.Angle = angleParcouru;

                    if (!_hasLeftStartZone && (diffAngulaire > 6.0 && diffAngulaire < 354.0))
                    {
                        _hasLeftStartZone = true;
                    }

                    // ⏱️ GESTION DU POPUP TEMPOREL (Anticipation de 1.5 seconde avant l'arrêt)
                    if (_hasLeftStartZone && !_isPopupShown && _currentRotationSpeed > 0 && !_oneTurnActive)
                    {
                        double tempsRestant = ComputeAutoCloseTimeRemaining();

                        if (tempsRestant <= 1.7)
                        {
                            _isPopupShown = true;

                            if (_oneTurnNextActive)
                                txtInfoPopup.Text = "Chargement panorama suivant ...";
                            else
                                txtInfoPopup.Text = "Fermeture automatique...";

                            if (AutoRotateFastSeconds < 6 && _autoRotState == AutoRotationState.Rapide)
                                (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                            else
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                        }
                    }

                    // Seuil d'entrée physique dans la zone d'arrêt
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

                if (_oneTurnNextActive)
                {
                    _oneTurnNextActive = false;
                    // _autoNextFromButton reste true → LoadNewPanorama s'en servira pour relancer
                    Dispatcher.BeginInvoke(new Action(() => NavigateToAdjacentPano(+1)));
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() => Window.GetWindow(this)?.Close()));
                }
                MajEtatAutoCloseAutoNext();
                return;
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
        #endregion Initialisation & Moteur 3D de rendu
        #region Evenements Clavier, souris et spacemouse
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

            // Navigation clavier entre panoramas
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewKeyDown += OnWindowKeyDown;
                window.PreviewKeyUp += OnWindowKeyUp;
            }

        }
        // ─────────────────────────────────────────────────────────────────────
        // Gestion du clavier
        // ─────────────────────────────────────────────────────────────────────
        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            ////Différents cas où le clavier n'est pas accessible
            //if (_OptionOpen) return;

            //if (_oneTurnActive) return;

            //if (_isMouseDown) return;

            //if (_autoRotState == AutoRotationState.Lent || _autoRotState == AutoRotationState.Normal || _autoRotState == AutoRotationState.Rapide) return;

            ////Fléches gauche et droites
            //if (e.Key == Key.Right)
            //{
            //    if (_autoCloseActive && !_oneTurnNextActive)
            //    {
            //        txtInfoPopup.Text = "Bouton désactivé dans ce mode";
            //        (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
            //        return;
            //    }

            //    e.Handled = true;
            //    _ = ShowMessageAndNavigateAsync(+1);
            //}
            //else if (e.Key == Key.Left)
            //{
            //    if (_autoCloseActive && !_oneTurnNextActive)
            //    {
            //        txtInfoPopup.Text = "Bouton désactivé dans ce mode";
            //        (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
            //        return;
            //    }

            //    e.Handled = true;
            //    _ = ShowMessageAndNavigateAsync(-1);
            //}
        }
        private void OnWindowKeyUp(object sender, KeyEventArgs e)
        {

        }
        // ─────────────────────────────────────────────────────────────────────
        // Gestion de la souris
        // ─────────────────────────────────────────────────────────────────────
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.None;

            if (_context != null) _context.BlocageShowCaption = true;

            if (e.MiddleButton == MouseButtonState.Pressed && !_OptionOpen)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(new Action(() => Window.GetWindow(this)?.Close()));
                return; // On sort immédiatement du handler
            }

            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (_autoRotState != AutoRotationState.Off)
            {
                txtInfoPopup.Text = "Arrêt rotation automatique";
                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);

                _autoNextFromButton = false;

                _autoRotState = AutoRotationState.Off;
                btnAutoRotate.Content = "Rotation auto.";
                btnAutoRotate.Background = Brushes.Transparent;
                MajEtatAutoCloseAutoNext();
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
            Mouse.OverrideCursor = null;

            //permet de réafficher la barre haute lorqu'on relache le clic de la souris
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
            // Pour la gestion des abus d'outils
            if (_isSpaceMouseZoom)
            {
                txtInfoPopup.Text = "Spacemouse l'emporte, arrête de jouer. 😉";
                (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                return;
            }

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

            if (_isMouseDown) return;

            //if (_oneTurnActive)
            //{
            //    txtInfoPopup.Text = "⌨️ Clavier désactivé dans ce mode";
            //    if (_oneTurnDuration > 6)
            //    {
            //        (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
            //    }
            //    else
            //    {
            //        (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
            //    }
            //    return;
            //}

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

                if (_autoRotState == AutoRotationState.Off)
                {
                    // Si aucune cible n'est définie, partir de l'angle actuel.
                    double AngleCourant = double.IsNaN(_targetHorizontalAngle) ? _horizontalRotation.Angle : _targetHorizontalAngle;
                    switch (keyCode)
                    {
                        case 9:
                            if (_autoCloseActive && !_oneTurnNextActive)
                            {
                                txtInfoPopup.Text = "Bouton désactivé dans ce mode";
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                                return;
                            }
                            _ = ShowMessageAndNavigateAsync(-1);

                            break;
                        case 3:
                            if (_autoCloseActive && !_oneTurnNextActive)
                            {
                                txtInfoPopup.Text = "Bouton désactivé dans ce mode";
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                                return;
                            }
                            _ = ShowMessageAndNavigateAsync(+1);
                            break;
                        case 6:
                            txtInfoPopup.Text = $"Rotation - {_camera.FieldOfView:F0}°";
                            (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                            _targetHorizontalAngle = NormalizeAngle(AngleCourant - _camera.FieldOfView);
                            break;
                        case 5:
                            txtInfoPopup.Text = $"Rotation + {_camera.FieldOfView:F0}°";
                            (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                            _targetHorizontalAngle = NormalizeAngle(AngleCourant + _camera.FieldOfView);
                            break;
                        case 11:
                            AutoRotate_ActionBouton();
                            AfficheEtatAutoRotation();
                            break;
                        case 32:
                            txtInfoPopup.Text = "🏠 Vue de départ";
                            (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                            _targetHorizontalAngle = _homeHorizontalAngle;
                            _targetVerticalAngle = _homeVerticalAngle;
                            break;
                    }
                }
                else  //cas si l'autorotation est enclenchée
                {
                    switch (keyCode)
                    {
                        //case 9 or 3 or 6 or 5 or 32:
                        case 6 or 5 or 32:
                            txtInfoPopup.Text = "⌨️ Touche désactivée dans ce mode";
                            if (AutoRotateFastSeconds < 6 && _autoRotState == AutoRotationState.Rapide)
                            {
                                (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);
                            }
                            else
                            {
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                            }
                            break;
                        case 9:
                            if (_autoCloseActive && !_oneTurnNextActive)
                            {
                                txtInfoPopup.Text = "Bouton désactivé dans ce mode";
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                                return;
                            }

                            _ = ShowMessageAndNavigateAsync(-1);

                            break;
                        case 3:
                            if (_autoCloseActive && !_oneTurnNextActive)
                            {
                                txtInfoPopup.Text = "Bouton désactivé dans ce mode";
                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                                return;
                            }

                            _ = ShowMessageAndNavigateAsync(+1);
                            break;
                        case 11:
                            AutoRotate_ActionBouton();
                            AfficheEtatAutoRotation();
                            break;
                    }
                }
            });
        }
        private void OnSpaceMouseKeyUp(int keyCode)
        {

        }
        #endregion Evenements Clavier, souris et spacemouse
        #region Options & Préférences
        // ─────────────────────────────────────────────────────────────────────
        // OPTIONS & PREFERENCES (Panneau et logique de lectrure/sauvegarde)
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
                        MiseAJourSettings();

                        // ✅ Paramètres visuels : différés après chargement complet de la fenêtre
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // FOV
                            MiseAJourFovUI();

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

                            //Barre de rating
                            if (_isBarreCompactee)
                            {
                                HideRatingPanelZoomEtOpacity();
                            }
                            else
                            {
                                ShowRatingPanelZoomEtOpacity();
                            }

                            // ── Application de l'autorotation si sélectionnée ────────────────
                            if (_StartRadioBouton == 2)
                            {
                                switch (_StartAutoRotate)
                                {
                                    case 1:
                                        _autoRotState = AutoRotationState.Lent;
                                        btnAutoRotate.Content = "🐢 Lent";
                                        btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                                        break;
                                    case 2:
                                        _autoRotState = AutoRotationState.Normal;
                                        btnAutoRotate.Content = "▶️ Normal";
                                        btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                                        break;
                                    case 3:
                                        _autoRotState = AutoRotationState.Rapide;
                                        btnAutoRotate.Content = "▶️▶️ Rapide";
                                        btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                                        break;
                                }
                                //_autoCloseActive = true;
                                btnAutoClose.IsEnabled = true;
                                txtAutoClose.Opacity = 1.0;
                                AfficheEtatAutoRotation();
                            }

                            // ── Application de 1 tour et on ferme ────────────────────────────
                            if (_StartOneTurnActive || _StartOneTurnNext)
                            {
                                _oneTurnActive = true;
                                _oneTurnNextActive = _StartOneTurnNext; // Mémorise lequel des deux modes est actif
                                _oneTurnTimer = 0.0;
                                _oneTurnStartAngle = _horizontalRotation.Angle;

                                // Sécurité au cas où la durée entrée est trop courte pour le profil trapézoïdal
                                if (_oneTurnDuration <= _oneTurnAccelTime * 2)
                                    _oneTurnAccelTime = _oneTurnDuration / 2.0;

                                // Calcul des lois physiques adaptées au temps imposé
                                _oneTurnVMax = 360.0 / (_oneTurnDuration - _oneTurnAccelTime);
                                _oneTurnAccelRate = _oneTurnVMax / _oneTurnAccelTime;

                                // Texte d'information pour l'utilisateur
                                txtInfoPopup.Text = _StartOneTurnNext
                                    ? "Rotation 1 tour + panorama suivant"
                                    : "Rotation 1 tour + fermeture";

                                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);

                                // Affiche un texte différent sur le bouton RotationAuto
                                btnAutoRotate.Content = "Rotation auto.";
                                btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                                btnAutoRotate.IsEnabled = false;

                                // Bouton Autoclose : activation et mise à jour de l'affichage
                                _autoCloseActive = true;
                                btnAutoClose.IsEnabled = true;
                                txtAutoClose.Opacity = 1.0;
                                txtAutoClose.Text = _StartOneTurnNext ? "1 tour + 📷 ▶️" : "1 tour + ✖️";
                                btnAutoClose.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                                panelAutoCloseProgress.Visibility = Visibility.Visible;

                                // Mise à jour de l'info popup sur la durée de l'autorotation
                                _autoCloseStartAngle = _horizontalRotation.Angle;
                                _autoCloseTargetAngle = _autoCloseStartAngle;
                                _hasLeftStartZone = false;
                                _isPopupShown = true;
                                _isPopupShownPour1Tour = false;
                                _isPopupShownPour1TourNum2 = false;
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
        private void MiseAJourSettings()
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
            _StartOneTurnNext = settings.StartOneTurnAndNext;
            _oneTurnDuration = settings.StartOneTurnDuration;

            // Memoires GPS
            _GpsMemoire1Nom = settings.GpsMemoire1Nom;
            _GpsMemoire1Latitude = settings.GpsMemoire1Latitude;
            _GpsMemoire1Longitude = settings.GpsMemoire1Longitude;
            _GpsMemoire1Zoom = settings.GpsMemoire1Zoom;
            _GpsMemoire2Nom = settings.GpsMemoire2Nom;
            _GpsMemoire2Latitude = settings.GpsMemoire2Latitude;
            _GpsMemoire2Longitude = settings.GpsMemoire2Longitude;
            _GpsMemoire2Zoom = settings.GpsMemoire2Zoom;
            _GpsMemoire3Nom = settings.GpsMemoire3Nom;
            _GpsMemoire3Latitude = settings.GpsMemoire3Latitude;
            _GpsMemoire3Longitude = settings.GpsMemoire3Longitude;
            _GpsMemoire3Zoom = settings.GpsMemoire3Zoom;

            // Derniers mots-clés utilisés (boite de dialogue Titre/Mots-clés)
            _DerniersMotsCles = settings.DerniersMotsCles ?? new List<string>();
            MettreAJourBoutonsSuggestionsMotsCles();

            // Permet de mettre a jour les textes des items dans le sous menu de la carte
            MettreAJourLibellesFavoris();

            // Mise à jour boite de dialogue: Fullscreen
            chkFullscreen.IsChecked = _chkFullscreenSavedValue;

            // Mise à jour boite de dialogue: Barre reduite
            chkBarreReduite.IsChecked = _OptionBarreReduite;

            // Mise à jour boite de dialogue: Fov
            sldFov.Value = _OptionFov;
            txtFov.Text = string.Format("(FOV: {0:F0}°)", _OptionFov);

            // Mise à jour boite de dialogue: durée autoclose
            sldAutoCloseDelay.Value = _oneTurnDuration;
            lblAutoCloseDelayValue.Text = _oneTurnDuration.ToString() + " s";

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
                    radStartAutoRotate.IsChecked = true;
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
                case 4:
                    radStartAutoSuivant.IsChecked = true;
                    _StartOneTurnActive = false;
                    _StartOneTurnNext = true;

                    btnOptionAutoRotate.Visibility = Visibility.Hidden;
                    TextAutoClose.Visibility = Visibility.Visible;
                    sldAutoCloseDelay.Visibility = Visibility.Visible;
                    lblAutoCloseDelayValue.Visibility = Visibility.Visible;
                    break;
            }
        }
        private void SaveSettings()
        {
            // Assigner les variables physiques locales pour exécution immédiate
            AutoRotateSlowSeconds = sldSlow.Value;
            AutoRotateNormalSeconds = sldNormal.Value;
            AutoRotateFastSeconds = sldFast.Value;

            _StartOneTurnActive = (_StartRadioBouton == 3);
            _StartOneTurnNext = (_StartRadioBouton == 4);

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
                    StartOneTurnAndNext = _StartOneTurnNext,
                    StartOneTurnDuration = (int)sldAutoCloseDelay.Value,
                    GpsMemoire1Nom = _GpsMemoire1Nom,
                    GpsMemoire1Latitude = _GpsMemoire1Latitude,
                    GpsMemoire1Longitude = _GpsMemoire1Longitude,
                    GpsMemoire1Zoom = _GpsMemoire1Zoom,
                    GpsMemoire2Nom = _GpsMemoire2Nom,
                    GpsMemoire2Latitude = _GpsMemoire2Latitude,
                    GpsMemoire2Longitude = _GpsMemoire2Longitude,
                    GpsMemoire2Zoom = _GpsMemoire2Zoom,
                    GpsMemoire3Nom = _GpsMemoire3Nom,
                    GpsMemoire3Latitude = _GpsMemoire3Latitude,
                    GpsMemoire3Longitude = _GpsMemoire3Longitude,
                    GpsMemoire3Zoom = _GpsMemoire3Zoom,
                    DerniersMotsCles = _DerniersMotsCles
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
            btnAutoRotate.Background = Brushes.Transparent;
            _autoCloseActive = false;
            MajEtatAutoCloseAutoNext();

            // Arrêt immédiat de 1tour et on ferme (ou on change)
            _oneTurnActive = false;
            _oneTurnNextActive = false;

            // Passe le bouton option en bleu pour montrer qu'il est sélectionné
            btnOptions.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));

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
            else if (radStartAutoSuivant.IsChecked == true)
            {
                _StartRadioBouton = 4;

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

            // Passe le bouton option en transparent
            btnOptions.Background = Brushes.Transparent;

            // Sauvegarde définitive
            SaveSettings();
        }
        private void SldFov_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblFovValue != null) lblFovValue.Text = string.Format("{0:F0}°", e.NewValue);
        }
        private void SldAutoCloseDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblAutoCloseDelayValue != null) lblAutoCloseDelayValue.Text = string.Format("{0:F0} s", e.NewValue);
        }
        private void SldSlow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblSlow != null) lblSlow.Text = string.Format("{0:F0} s", e.NewValue);
        }
        private void SldNormal_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblNormal != null) lblNormal.Text = string.Format("{0:F0} s", e.NewValue);
        }
        private void SldFast_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblFast != null) lblFast.Text = string.Format("{0:F0} s", e.NewValue);
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
        #endregion Options & Préférences
        #region Boutons de l'interface
        // ─────────────────────────────────────────────────────────────────────
        // Aspect visuel des boutons barre de boutons (FadeIn et FadeOut)
        // ─────────────────────────────────────────────────────────────────────
        private void BarreBtn_MouseEnter(object sender, MouseEventArgs e)
        {
            _isMouseOverBarre = true;
            UpdateBarreOpacity(); // Force la réapparition immédiate
        }
        private void BarrePanelRating_MouseEnter(object sender, MouseEventArgs e)
        {
            _isMouseOverBarre = true;
            UpdateBarreOpacity(); // Force la réapparition immédiate
        }
        private void BarreBtn_MouseLeave(object sender, MouseEventArgs e)
        {
            // On remet une "bûche" dans le compteur pour donner un petit sursis avant que ça ne re-disparaisse
            _isMouseOverBarre = false;
        }
        private void BarrePanelRating_MouseLeave(object sender, MouseEventArgs e)
        {
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
                    (panelRating.Resources["FadeInRating"] as Storyboard)?.Begin(panelRating);
                    (btnToggleInfo.Resources["FadeInbtnToggleInfo"] as Storyboard)?.Begin(btnToggleInfo);
                    (btnToggleMap.Resources["FadeInbtnToggleMap"] as Storyboard)?.Begin(btnToggleMap);
                    (btnDragDrop.Resources["FadeInbtnDragDrop"] as Storyboard)?.Begin(btnDragDrop);
                    _isBarreMasquee = false;
                }
                return;
            }

            // Détermination si le panorama est considéré "en mouvement"
            // 1. Soit la souris est enfoncée (drag)
            // 2. Soit l'autorotation est active
            // 3. Soit le dernier mouvement enregistré est plus récent que le délai d'inactivité
            bool isActuellementEnMouvement = _isMouseDown || (_autoRotState != AutoRotationState.Off) || (DateTime.Now - _lastMovementTime).TotalSeconds < InactivityDelay;

            if (isActuellementEnMouvement)
            {
                // Le panorama bouge : on applique le fondu transparent (FadeOut)
                if (!_isBarreMasquee)
                {
                    (barreBtn.Resources["FadeOutBarreBtn"] as Storyboard)?.Begin(barreBtn);
                    (panelAutoCloseProgress.Resources["FadeOutProgress"] as Storyboard)?.Begin(panelAutoCloseProgress);
                    (btnToggleInfo.Resources["FadeOutbtnToggleInfo"] as Storyboard)?.Begin(btnToggleInfo);
                    (btnToggleMap.Resources["FadeOutbtnToggleMap"] as Storyboard)?.Begin(btnToggleMap);
                    (btnDragDrop.Resources["FadeOutbtnDragDrop"] as Storyboard)?.Begin(btnDragDrop);
                    if (_autoCloseActive || _autoRotState != AutoRotationState.Off)
                    {
                        (panelRating.Resources["FadeOutRating"] as Storyboard)?.Begin(panelRating);
                    }
                    else
                    {
                        (panelRating.Resources["FadeOutFullRating"] as Storyboard)?.Begin(panelRating);
                    }

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
                    (panelRating.Resources["FadeInRating"] as Storyboard)?.Begin(panelRating);
                    (btnToggleInfo.Resources["FadeInbtnToggleInfo"] as Storyboard)?.Begin(btnToggleInfo);
                    (btnToggleMap.Resources["FadeInbtnToggleMap"] as Storyboard)?.Begin(btnToggleMap);
                    (btnDragDrop.Resources["FadeInbtnDragDrop"] as Storyboard)?.Begin(btnDragDrop);
                    _isBarreMasquee = false;
                }
                _context.BlocageShowCaption = false;
            }
        }
        private void TxtFov_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                MarquerMouvement();

                _isBarreCompactee = !_isBarreCompactee;

                //Barre de rating
                if (_isBarreCompactee)
                {
                    HideRatingPanelZoomEtOpacity();
                }
                else
                {
                    ShowRatingPanelZoomEtOpacity();
                }

                // Le rating qui apparaît peut désormais chevaucher un panneau carte déjà agrandi
                // (ex: redimensionné en plein écran, puis sortie du plein écran sans re-déclenchement)
                if (_isMapPanelVisible)
                    ContraindreTailleMap();

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
        // BOUTONS panorama précedent / Suivant
        // ─────────────────────────────────────────────────────────────────────
        private void BtnBarrePrevPano_Click(object sender, RoutedEventArgs e)
        {
            if (_autoCloseActive && !_oneTurnNextActive)
            {
                txtInfoPopup.Text = "Bouton désactivé dans ce mode";
                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                return;
            }

            _ = ShowMessageAndNavigateAsync(-1);
            e.Handled = true;
        }
        private void BtnBarreNextPano_Click(object sender, RoutedEventArgs e)
        {
            if (_autoCloseActive && !_oneTurnNextActive)
            {
                txtInfoPopup.Text = "Bouton désactivé dans ce mode";
                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                return;
            }

            _ = ShowMessageAndNavigateAsync(+1);
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
                    _autoNextSavedRotState = _autoRotState;
                    btnAutoRotate.Content = "🐢 Lent";
                    btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                    break;

                case AutoRotationState.Lent:
                    _autoRotState = AutoRotationState.Normal;
                    _autoNextSavedRotState = _autoRotState;
                    btnAutoRotate.Content = "▶️ Normal";
                    btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                    break;

                case AutoRotationState.Normal:
                    _autoRotState = AutoRotationState.Rapide;
                    _autoNextSavedRotState = _autoRotState;
                    btnAutoRotate.Content = "▶️▶️ Rapide";
                    btnAutoRotate.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                    break;

                case AutoRotationState.Rapide:
                    _autoRotState = AutoRotationState.Off;
                    btnAutoRotate.Content = "Rotation auto.";
                    btnAutoRotate.Background = Brushes.Transparent;
                    _lastMovementTime = DateTime.Now;
                    break;
            }
            MajEtatAutoCloseAutoNext();
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
        // BOUTON AUTOCLOSE & AUTONEXT 
        // ─────────────────────────────────────────────────────────────────────
        private void BtnAutoCloseAutoNext_Click(object sender, RoutedEventArgs e)
        {
            if (_oneTurnActive)
            {
                // Si l’utilisateur clique sur le bouton alors que l'autorotation au démarrage est activée, on lui rend la main
                txtInfoPopup.Text = "Arrêt rotation automatique";
                (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
                _oneTurnActive = false;
                _oneTurnNextActive = false; // ← AJOUT
                _StartOneTurnNext = false;
                _autoCloseActive = false;
                btnAutoRotate.Content = "Rotation auto.";
                btnAutoRotate.Background = Brushes.Transparent;
                btnAutoRotate.IsEnabled = true;
                _isPopupShown = false;
                MajEtatAutoCloseAutoNext();

                return;
            }

            if (_autoRotState == AutoRotationState.Off) return;

            if (!_autoCloseActive && !_oneTurnNextActive)
            {
                // Off → État 1 : AutoClose (1 tour + fermeture)
                _autoCloseActive = true;
                _oneTurnNextActive = false;
                _autoCloseStartAngle = _horizontalRotation.Angle;
                _autoCloseTargetAngle = _autoCloseStartAngle;
                _hasLeftStartZone = false;
                _isPopupShown = false;
            }
            else if (_autoCloseActive && !_oneTurnNextActive)
            {
                // État 1 → État 2 : AutoNext (1 tour + panorama suivant)
                // On GARDE _autoCloseActive = true : c'est lui qui pilote le moteur de suivi
                _autoCloseActive = true;
                _oneTurnNextActive = true;
                _autoNextFromButton = true;
                _autoNextSavedRotState = _autoRotState;
                // On repart de l'angle actuel pour repartir d'un tour complet
                _autoCloseStartAngle = _horizontalRotation.Angle;
                _autoCloseTargetAngle = _autoCloseStartAngle;
                _hasLeftStartZone = false;
                _isPopupShown = false;
                _isAutoClosingPhase = false;
            }
            else
            {
                // État 2 → Off
                _autoCloseActive = false;
                _oneTurnNextActive = false;
                _autoNextFromButton = false;
                _autoCloseTargetAngle = -1;
                _autoCloseStartAngle = -1;
                _hasLeftStartZone = false;
                _isAutoClosingPhase = false;
            }

            MajEtatAutoCloseAutoNext();
        }
        private void MajEtatAutoCloseAutoNext()
        {
            if (_autoRotState == AutoRotationState.Off)
            {
                _autoCloseActive = false;
                _oneTurnNextActive = false;
                _isAutoClosingPhase = false;
                _autoCloseTargetAngle = -1;
                _autoCloseStartAngle = -1;
                _hasLeftStartZone = false;

                btnAutoClose.IsEnabled = false;
                txtAutoClose.Text = "1 tour + ... : Off";
                txtAutoClose.Opacity = 0.5;
                panelAutoCloseProgress.Visibility = Visibility.Collapsed;
            }
            else
            {
                btnAutoClose.IsEnabled = true;
                txtAutoClose.Opacity = 1.0;
                txtAutoClose.TextDecorations = null;

                if (_autoCloseActive && _oneTurnNextActive)
                {
                    // État 2 : 1 tour + panorama suivant
                    txtAutoClose.Text = "1 tour + 📷 ▶️";
                    btnAutoClose.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                    panelAutoCloseProgress.Visibility = Visibility.Visible;
                }
                else if (_autoCloseActive)
                {
                    // État 1 : 1 tour + fermeture
                    txtAutoClose.Text = "1 tour + ✖️";
                    btnAutoClose.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4400AAFF"));
                    panelAutoCloseProgress.Visibility = Visibility.Visible;
                }
                else
                {
                    // État 0 : désactivé
                    txtAutoClose.Text = "1 tour + ... : Off";
                    btnAutoClose.Background = Brushes.Transparent;
                    panelAutoCloseProgress.Visibility = Visibility.Collapsed;
                }
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // BOUTON Drag & Drop 
        // ─────────────────────────────────────────────────────────────────────
        private void BtnDragDrop_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPanoPath) || !System.IO.File.Exists(_currentPanoPath))
                return;

            e.Handled = true; // Empêche le bouton de déclencher son propre Click/effet visuel pendant le drag

            var dataObject = new DataObject(DataFormats.FileDrop, new string[] { _currentPanoPath });
            DragDrop.DoDragDrop(btnDragDrop, dataObject, DragDropEffects.Copy | DragDropEffects.Link);
        }
        #endregion Boutons de l'interface
        #region Navigation panoramas
        // ─────────────────────────────────────────────────────────────────────
        // NAVIGATION entre panoramas du dossier courant
        // ─────────────────────────────────────────────────────────────────────
        private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Retourne la liste triée des fichiers image du même dossier que le panorama courant.
            // Seules les extensions reconnues sont conservées.
            ".jpeg", ".jpg",
            ".tif", ".tiff",
            ".webp"
        };
        private List<string> GetPanoFilesInFolder()
        {
            string folder = System.IO.Path.GetDirectoryName(_currentPanoPath);
            if (folder == null || !System.IO.Directory.Exists(folder))
                return new List<string>();

            return System.IO.Directory
                .EnumerateFiles(folder)
                .Where(f => _imageExtensions.Contains(System.IO.Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        private void NavigateToAdjacentPano(int direction)
        {
            // ÉTAPE 1 : Sauvegarde automatique des métadonnées du panorama actuel avant de changer de panorama
            if (_isMetaChanging) SauvegardeMeta();

            // ÉTAPE 2 :  Tente de naviguer vers le panorama suivant (direction=+1) ou précédent (direction=-1).
            // Saute les images qui ne sont pas équirectangulaires.
            // Boucle en fin/début de liste.
            if (_isNavigating) return;

            var files = GetPanoFilesInFolder();
            if (files.Count < 2) return;

            int currentIndex = files.FindIndex(
                f => string.Equals(f, _currentPanoPath, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0) return;   // fichier courant introuvable dans la liste

            int tested = 0;
            int candidate = currentIndex;

            while (tested < files.Count - 1)
            {
                // Avance circulairement
                candidate = (candidate + direction + files.Count) % files.Count;
                tested++;

                string candidatePath = files[candidate];

                // Test équirectangulaire (utilise la même détection que Plugin.cs)
                MetaProvider meta;
                try
                {
                    meta = new MetaProvider(candidatePath);
                }
                catch
                {
                    continue; // Fichier illisible → on passe
                }

                if (!EquirectangularDetector.IsEquirectangular(meta))
                    continue;   // Pas un panorama → on passe au suivant

                // On a trouvé un candidat valide
                LoadNewPanorama(candidatePath);
                return;
            }

            // Aucun autre panorama trouvé dans le dossier
            txtInfoPopup.Text = "Aucun autre panorama dans ce dossier";
            (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);
        }
        private void LoadNewPanorama(string newPath)
        {
            // Recharge la texture du panorama sur la sphère existante avec un micro-fondu noir.
            // Ne recrée pas la sphère ni la caméra : seule la texture change.
            if (_isNavigating) return;
            _isNavigating = true;

            // ── Swap de texture (sur le thread UI) ───────────────
            try
            {
                var material = CreatePanoramaMaterial(newPath);

                // Retrouver le GeometryModel3D dans le viewport pour changer son matériau
                foreach (var child in viewport3D.Children)
                {
                    if (child is ModelVisual3D mv && mv.Content is GeometryModel3D gm)
                    {
                        gm.Material = material;
                        gm.BackMaterial = material;
                        break;
                    }
                }

                // Mise à jour de l'état courant
                _currentPanoPath = newPath;
                _context.Title = $"360° : {System.IO.Path.GetFileName(newPath)}";

                // lire les données Exif/Xmp
                LoadAndDisplayMetadata(_currentPanoPath);
                _isMetaChanging = false;                         // initialise l'état de l'indicateur de sauvegarde

                // Mise à jour avec la position Home (vide ou enregistrée)
                _horizontalRotation.Angle = _homeHorizontalAngle;
                _verticalRotation.Angle = _homeVerticalAngle;

                // FOV
                MiseAJourFovUI();

                // Permet d'afficher le panneau d'information et de retirer une partie si les informations sont manquantes
                AffichagePanneauxInfosSansAnimations();

                // Démarrage
                if (_StartOneTurnNext)
                    DemarrerOneTurnNext();
                else if (_autoNextFromButton)
                    DemarrerAutoNextFromButton();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("(LoadNewPanorama)Erreur chargement :" + ex.Message);
            }
            finally
            {
                _isNavigating = false;
            }
        }
        private void DemarrerAutoNextFromButton()
        {
            // Restaure la vitesse d'autorotation qui était active au moment du clic
            _autoRotState = _autoNextSavedRotState;
            switch (_autoNextSavedRotState)
            {
                case AutoRotationState.Lent: btnAutoRotate.Content = "🐢 Lent"; break;
                case AutoRotationState.Normal: btnAutoRotate.Content = "▶️ Normal"; break;
                case AutoRotationState.Rapide: btnAutoRotate.Content = "▶️▶️ Rapide"; break;
            }

            _autoCloseActive = true;
            _oneTurnNextActive = true;

            _autoCloseStartAngle = _horizontalRotation.Angle;
            _autoCloseTargetAngle = _autoCloseStartAngle;
            _hasLeftStartZone = false;
            _isPopupShown = false;
            _isAutoClosingPhase = false;

            MajEtatAutoCloseAutoNext();
        }
        private void DemarrerOneTurnNext()
        {
            // Gestionnaires boutons ◀ / ▶ de la barre
            _oneTurnActive = true;
            _oneTurnNextActive = true;
            _oneTurnTimer = 0.0;
            _oneTurnStartAngle = _horizontalRotation.Angle;

            if (_oneTurnDuration <= _oneTurnAccelTime * 2)
                _oneTurnAccelTime = _oneTurnDuration / 2.0;

            _oneTurnVMax = 360.0 / (_oneTurnDuration - _oneTurnAccelTime);
            _oneTurnAccelRate = _oneTurnVMax / _oneTurnAccelTime;

            _autoCloseActive = false;
            _isAutoClosingPhase = false;
            _autoCloseStartAngle = -1;
            _autoCloseTargetAngle = -1;
            _hasLeftStartZone = false;
            _isPopupShown = false;
            _isPopupShownPour1Tour = false;
            _isPopupShownPour1TourNum2 = false;

            txtAutoClose.Opacity = 1.0;
            txtAutoClose.Text = "1 tour + 📷 ▶️";
            panelAutoCloseProgress.Visibility = Visibility.Visible;

            btnAutoRotate.IsEnabled = false;
        }
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // Logique de détection d'inactivité et de préchargement des panoramas suivants (previous/next)
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private async void OnIdleTimerTick(object sender, EventArgs e)
        {
            // Vérification stricte : aucun mouvement utilisateur, aucun mode auto, et vitesse à zéro
            bool isIdle = !_isMouseDown &&
                          _autoRotState == AutoRotationState.Off &&
                          !_oneTurnActive &&
                          Math.Abs(_currentRotationSpeed) < 0.01 &&
                          (DateTime.Now - _lastMovementTime).TotalSeconds > 1.0; // 1 seconde de répit confirmée

            if (isIdle && !_isPreloading)
            {
                await PreloadImagesAsync();
            }
        }
        private async Task PreloadImagesAsync()
        {
            _isPreloading = true;
            try
            {
                var files = GetPanoFilesInFolder();
                if (files.Count < 2) return;

                // 1. La "Liste Blanche" des fichiers qui ont le droit de rester en RAM
                var validPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _currentPanoPath };

                // Liste ordonnée des fichiers à charger (du plus proche au plus lointain)
                var pathsToLoad = new List<string>();

                // On balaie l'horizon dynamiquement
                for (int i = 1; i <= _preloadHorizon; i++)
                {
                    string nextPath = GetAdjacentPanoPath(i, files);
                    string prevPath = GetAdjacentPanoPath(-i, files);

                    // On ajoute le suivant (priorité 1)
                    if (nextPath != null && validPaths.Add(nextPath)) pathsToLoad.Add(nextPath);
                    // On ajoute le précédent (priorité 2)
                    if (prevPath != null && validPaths.Add(prevPath)) pathsToLoad.Add(prevPath);
                }

                // 2. Le Nettoyeur (Garbage Collector manuel)
                // On supprime du dictionnaire tout ce qui n'est PAS dans la liste blanche
                var keysToRemove = _imageCache.Keys.Where(k => !validPaths.Contains(k)).ToList();
                foreach (var k in keysToRemove)
                {
                    _imageCache.Remove(k);
                }

                // On met à jour l'UI juste après le nettoyage
                UpdateCacheVisuals();

                // 3. Le Chargeur
                // On parcourt la liste ordonnée. Les fichiers +1 et -1 seront traités avant les +2 et -2.
                foreach (var path in pathsToLoad)
                {
                    if (!_imageCache.ContainsKey(path))
                    {
                        await LoadImageToCacheAsync(path);

                        // Appels Étape 2 : À chaque fois qu'UNE image a fini de charger,
                        // l'UI se met à jour immédiatement sans attendre les autres !
                        UpdateCacheVisuals();
                    }
                }
            }
            finally
            {
                _isPreloading = false;
            }
        }
        private async Task LoadImageToCacheAsync(string path)
        {
            try
            {
                // Le décodage lourd se fait sur un Thread du ThreadPool pour ne pas bloquer l'UI
                var bmp = await Task.Run(() =>
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.CacheOption = BitmapCacheOption.OnLoad; // Oblige la lecture du fichier immédiatement
                    image.EndInit();

                    // CRUCIAL : "Gèle" l'image pour la rendre thread-safe et accessible à l'UI
                    image.Freeze();
                    return image;
                });

                // De retour sur le thread UI, on injecte l'image prête à l'emploi
                _imageCache[path] = bmp;
                System.Diagnostics.Debug.WriteLine($"[Cache] Préchargé : {System.IO.Path.GetFileName(path)}");
            }
            catch
            {
                // Si le fichier est illisible ou corrompu, on l'ignore silencieusement
            }
        }
        private string GetAdjacentPanoPath(int direction, List<string> files)
        {
            // C'est une déclinaison muette de NavigateToAdjacentPano, sans la navigation
            int currentIndex = files.FindIndex(f => string.Equals(f, _currentPanoPath, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0) return null;

            int tested = 0;
            int candidate = currentIndex;

            while (tested < files.Count - 1)
            {
                candidate = (candidate + direction + files.Count) % files.Count;
                tested++;
                string candidatePath = files[candidate];

                try
                {
                    MetaProvider meta = new MetaProvider(candidatePath);
                    if (EquirectangularDetector.IsEquirectangular(meta))
                        return candidatePath;
                }
                catch { continue; }
            }
            return null;
        }
        private void UpdateCacheVisuals()
        {
            // On s'assure d'être sur le thread UI pour toucher aux composants graphiques
            Dispatcher.VerifyAccess();

            var files = GetPanoFilesInFolder();
            if (files == null || files.Count < 2) return;

            int prevCachedCount = 0;
            int nextCachedCount = 0;

            // 1. On compte ce qui est réellement en cache actuellement
            for (int i = 1; i <= _preloadHorizon; i++)
            {
                string nextPath = GetAdjacentPanoPath(i, files);
                if (nextPath != null && _imageCache.ContainsKey(nextPath)) nextCachedCount++;

                string prevPath = GetAdjacentPanoPath(-i, files);
                if (prevPath != null && _imageCache.ContainsKey(prevPath)) prevCachedCount++;
            }

            // Déclenchement des animations progressives
            AnimateArrow("AnimatePrevArrow", prevCachedCount);
            AnimateArrow("AnimateNextArrow", nextCachedCount);

            // Log en temps réel pour tes tests
            //System.Diagnostics.Debug.WriteLine($"[Cache UI] Précédents: {prevCachedCount} | Suivants: {nextCachedCount}");
        }
        private void AnimateArrow(string storyboardKey, int count)
        {
            if (this.Resources[storyboardKey] is Storyboard sb)
            {
                // 1. Calcul du ratio (0.0 à 1.0)
                double ratio = _preloadHorizon > 0 ? (double)count / _preloadHorizon : 1.0;
                if (ratio > 1.0) ratio = 1.0;

                // 2. Alignement sur ton choix : 
                // 0% chargé (ratio 0) = 0 (Noir total)
                // 100% chargé (ratio 1) = 255 (Blanc pur)
                byte gray = (byte)(255 * ratio);
                Color targetColor = Color.FromRgb(gray, gray, gray);

                // 3. Injection de la couleur cible dans le Storyboard
                if (sb.Children.Count > 0 && sb.Children[0] is ColorAnimation anim)
                {
                    anim.To = targetColor;
                }

                // 4. Lancement de la transition fluide
                sb.Begin();
            }
        }
        #endregion Navigation panoramas
        #region Gestion Exif et Xmp
        // ─────────────────────────────────────────────────────────────────────
        // Gestion Exif et Xmp
        // ─────────────────────────────────────────────────────────────────────
        private void LoadAndDisplayMetadata(string filePath)
        {
            // Gère la lecture et l'affichage de la note
            _currentPanoPath = filePath;

            // ─────── Étape 1 : Lecture via notre service ──────────────────────────
            _currentMetadata = PanoMetadataService.ReadMetadata(filePath);

            // ─────── Étape 2 : Mise à jour de l'affichage des étoiles ─────────────
            UpdateRatingUI(_currentMetadata.Rating ?? 0);

            // ─────── Étape 3 : Mise à jour des couleurs ───────────────────────────
            SetColorPanelRating(_currentMetadata.Label);

            // ─────── Étape 4 : Mise à jour du panneau d'informations ──────────────
            UpdateDataPanelInfo();

            // ─────── Étape 5 : Mise a jour de la vue "home" ───────────────────────
            _homeHorizontalAngle = _currentMetadata.HomeHorizontalSphere ?? 0.0;
            _homeVerticalAngle = _currentMetadata.HomeVerticalSphere ?? 0.0;
            _homeFov = _currentMetadata.HomeFovSphere ?? 0.0;
            _zoomPinMap = _currentMetadata.MapZoomLevel ?? 15;

            // ─────── Étape 6 : GPS ────────────────────────────────────────────────
            // Permet de précharger la carte
            _ = AssurerCarteInitialiseeAsync();

            // Met à jour le slider pour l'orientation du nord de la carte
            _sliderHeadingMisAJourProgrammatique = true;

            if (_currentMetadata.PoseHeadingDegrees != null)
            {
                sliderHeading.Value = _currentMetadata.PoseHeadingDegrees.Value;
            }
            else
            {
                sliderHeading.Value = 0;
            }

            _sliderHeadingMisAJourProgrammatique = false;

            // Si le panneau carte est déjà ouvert, on met à jour le marqueur pour le nouveau panorama
            if (_isMapPanelVisible)
            {
                AfficherPositionSurCarte(_currentMetadata.GpsLatitude, _currentMetadata.GpsLongitude);
                _ = MettreAJourFovSurCarte();
            }
        }
        private void SetColorPanelRating(string couleur)
        {
            switch (couleur)
            {
                case "Vert":
                    panelRating.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2004FF00"));
                    SetPointColorOnPanelRating(Brushes.Green);
                    break;
                case "Rouge":
                    panelRating.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30FF2B00"));
                    SetPointColorOnPanelRating(Brushes.Red);
                    break;
                case "Jaune":
                    panelRating.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#25FFFB00"));
                    SetPointColorOnPanelRating(Brushes.Yellow);
                    break;
                case "Bleu":
                    panelRating.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#300077FF"));
                    SetPointColorOnPanelRating(Brushes.Blue);
                    break;
                case "Violet":
                    panelRating.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#25AA00FF"));
                    SetPointColorOnPanelRating(Brushes.Purple);
                    break;
                default:
                    panelRating.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#80000000"));
                    SetPointColorOnPanelRating(Brushes.Gray);
                    break;
            }
        }
        private void SetPointColorOnPanelRating(Brush brush)
        {
            // Permet de synchroniser la couleur des points gauche et droit dans la barre de notation
            PointColorLeft.Foreground = brush;
            PointColorRight.Foreground = brush;
        }
        private void UpdateRatingUI(int rating)
        {
            // Colore les étoiles en fonction de la note(de 0 à 5)
            // On boucle sur nos 5 TextBlocks d'étoiles définis dans le XAML
            for (int i = 1; i <= 5; i++)
            {
                var starBtn = this.FindName($"Star{i}") as System.Windows.Controls.Button;
                if (starBtn != null)
                {
                    // Si l'index est inférieur ou égal à la note, l'étoile est pleine, sinon vide (grise)
                    starBtn.Content = i <= rating ? "★" : "☆";
                    starBtn.Foreground = i <= rating ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray;
                }
            }
        }
        private void Star_Click(object sender, RoutedEventArgs e)
        {
            // Gestion du clic sur une étoile pour changer la note
            if (sender is System.Windows.Controls.Button clickedButton)
            {
                // On récupère le numéro de l'étoile cliquée via sa propriété Tag
                if (short.TryParse(clickedButton.Tag?.ToString(), out short newRating))
                {
                    // Si on clique sur l'étoile 1 alors que la note est déjà 1, on bascule à 0 (annulation)
                    if (_currentMetadata.Rating == newRating && newRating == 1)
                        _currentMetadata.Rating = 0;
                    else
                        _currentMetadata.Rating = newRating;

                    // On rafraîchit l'affichage graphique
                    UpdateRatingUI(_currentMetadata.Rating ?? 0);

                    _isMetaChanging = true;      // Indique qu'il est nécessaire de sauvegarder

                    System.Diagnostics.Debug.WriteLine($"Nouvelle note demandée : {_currentMetadata.Rating}");
                }
            }
        }
        private void Color_Click(object sender, RoutedEventArgs e)
        {
            switch (_currentMetadata.Label)
            {
                case "Vert":
                    _currentMetadata.Label = "Rouge";
                    break;
                case "Rouge":
                    _currentMetadata.Label = "Jaune";
                    break;
                case "Jaune":
                    _currentMetadata.Label = "Bleu";
                    break;
                case "Bleu":
                    _currentMetadata.Label = "Violet";
                    break;
                case "Violet":
                    _currentMetadata.Label = "";
                    break;
                default:
                    _currentMetadata.Label = "Vert";
                    break;
            }
            // Indique qu'il est nécessaire de sauvegarder
            _isMetaChanging = true;

            // Mise à jour des couleurs de l'interface
            SetColorPanelRating(_currentMetadata.Label);

        }
        public void SauvegardeMeta()
        {
            // Texte d'information pour l'utilisateur
            txtInfoPopup.Text = "💾 Mise à jour des metadonnées 📷";
            (infoPopup.Resources["StoryboardShowInfo"] as Storyboard)?.Begin(infoPopup);

            // Debug
            //System.Diagnostics.Debug.WriteLine("--Debut sauvegarde--");
            //System.Diagnostics.Debug.WriteLine($"Rating : {_currentMetadata.Rating}");
            //System.Diagnostics.Debug.WriteLine($"Label : {_currentMetadata.Label}");
            //System.Diagnostics.Debug.WriteLine("--Fin sauvegarde--");

            //sauvegarde dans le fichier
            if (!string.IsNullOrEmpty(_currentPanoPath) && _currentMetadata != null)
            {
                PanoMetadataService.WriteMetadata(_currentPanoPath, _currentMetadata);
            }
        }
        #endregion Gestion Exif et Xmp
        #region Information
        // ─────────────────────────────────────────────────────────────────────
        // Panneau d'information
        // ─────────────────────────────────────────────────────────────────────
        private void UpdateDataPanelInfo()
        {
            // Le titre
            txtXmpTitle.Text = _currentMetadata.Titre ?? "";
            // Les mots clés
            AfficherMotsCles(_currentMetadata, txtXmpKeywords);
            // Le nom de fichier
            txtMetaFileName.Text = System.IO.Path.GetFileName(_currentPanoPath);
            // La date
            txtMetaDate.Text = _currentMetadata.Date ?? "--/--/--";
            // L'heure
            txtMetaTime.Text = _currentMetadata.Time ?? "--:--:--";
            // Camera
            txtMetaCamera.Text = _currentMetadata.Model ?? "--";
            // Taille du fichier
            PanelInfoSizeFile();
            // Iso
            if (_currentMetadata.Iso.HasValue)
            {
                txtMetaIso.Text = string.Format("{0:F0} ISO", _currentMetadata.Iso); 
            }
            else
            {
                txtMetaIso.Text = "-- ISO";
            }
            // Vitesse
            txtMetaShutter.Text = _currentMetadata.ShutterSpeed ?? "-/-";
        }
        public static void AfficherMotsCles(PanoMetadata data, TextBlock textBlock)
        {
            if (data.Keywords != null && data.Keywords.Count > 0)
            {
                textBlock.Text = string.Join(" · ", data.Keywords);
            }
            else
            {
                textBlock.Text = "Aucun mot clé";
            }
        }
        private void PanelInfoSizeFile()
        {
            // ─── 1. CALCUL DE LA TAILLE DU FICHIER SUR LE DISQUE ───
            string tailleFichierStr = "0 Mo";
            try
            {
                if (System.IO.File.Exists(_currentPanoPath))
                {
                    var fileInfo = new System.IO.FileInfo(_currentPanoPath);
                    // Conversion en Mo avec une décimale
                    double tailleMo = (double)fileInfo.Length / (1024 * 1024);
                    tailleFichierStr = string.Format("{0:F1}Mo", tailleMo);
                }
            }
            catch
            {
                tailleFichierStr = "??Mo";
            }

            // ─── 2. CALCUL DES MILLIONS DE PIXELS (Mpx) ───
            string mpxStr = "??Mpx";
            try
            {
                double pixelWidth = 0;
                double pixelHeight = 0;

                // On regarde si l'image est actuellement dans le cache RAM
                if (_imageCache.TryGetValue(_currentPanoPath, out var cachedImg))
                {
                    pixelWidth = cachedImg.PixelWidth;
                    pixelHeight = cachedImg.PixelHeight;
                }
                else
                {
                    // Si pas en cache, on lit uniquement les métadonnées de l'en-tête (très rapide grâce à DecodePixelWidth/Height ou DelayCreation)
                    var imgDecoder = new BitmapImage();
                    imgDecoder.BeginInit();
                    imgDecoder.UriSource = new Uri(_currentPanoPath, UriKind.Absolute);
                    imgDecoder.DecodePixelWidth = 1; // Demande minimale juste pour forcer la lecture des dimensions de l'en-tête
                    imgDecoder.CacheOption = BitmapCacheOption.None;
                    imgDecoder.EndInit();

                    // Note : Si DecodePixelWidth écrase les dimensions réelles dans l'objet, 
                    // vous pouvez utiliser un BitmapDecoder à la place qui est encore plus propre :
                    using (var stream = new System.IO.FileStream(_currentPanoPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                    {
                        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                        if (decoder.Frames.Count > 0)
                        {
                            pixelWidth = decoder.Frames[0].PixelWidth;
                            pixelHeight = decoder.Frames[0].PixelHeight;
                        }
                    }
                }

                if (pixelWidth > 0 && pixelHeight > 0)
                {
                    // Calcul des Mpx : (Largeur * Hauteur) / 1 000 000
                    double mpx = (pixelWidth * pixelHeight) / 1000000.0;
                    mpxStr = string.Format("{0:F0}Mpx", mpx); // F0 pour un arrondi à l'entier comme "120Mpx", ou F1 pour "120.4Mpx"
                }
            }
            catch
            {
                mpxStr = "??Mpx";
            }

            // ─── 3. AFFICHAGE DE LA CHAÎNE COMBINÉE ───
            txtMetaSize.Text = $"{tailleFichierStr} / {mpxStr}";
        }
        private void BtnToggleInfo_Click(object sender, RoutedEventArgs e)
        {
            _isPanelVisible = true;

            AffichagePanneauxInfos();

            e.Handled = true;
        }
        private void InfoExifXmp_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanelVisible = false;

            AffichagePanneauxInfos();

            e.Handled = true;
        }
        private void InfoTitleKeywords_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanelVisible = false;

            AffichagePanneauxInfos();

            e.Handled = true;
        }
        private void AffichagePanneauxInfos()
        {
            var sbIn = (Storyboard)FindResource("FadeInZoomIn");
            var sbOut = (Storyboard)FindResource("FadeOutZoomOut");

            if (_isPanelVisible)
            {
                btnToggleInfo.Visibility = Visibility.Collapsed;
                AppliquerVisibiliteGrids();

                pnlInfoContainer.Visibility = Visibility.Visible;
                sbIn.Begin(pnlInfoContainer);
            }
            else
            {
                EventHandler onCompleted = null;
                onCompleted = (s, e) =>
                {
                    sbOut.Completed -= onCompleted;

                    infoExifXmp.Visibility = Visibility.Collapsed;
                    infoTitleKeywords.Visibility = Visibility.Collapsed;
                    pnlInfoContainer.Visibility = Visibility.Collapsed;

                    btnToggleInfo.Visibility = Visibility.Visible;
                };

                sbOut.Completed += onCompleted;
                sbOut.Begin(pnlInfoContainer);
            }
        }
        private void AffichagePanneauxInfosSansAnimations()
        {
            if (_isPanelVisible)
            {
                btnToggleInfo.Visibility = Visibility.Collapsed;
                AppliquerVisibiliteGrids();
                pnlInfoContainer.Visibility = Visibility.Visible;
                pnlInfoContainer.Opacity = 1;
                pnlInfoScale.ScaleX = 1;
                pnlInfoScale.ScaleY = 1;
            }
            else
            {
                infoExifXmp.Visibility = Visibility.Collapsed;
                infoTitleKeywords.Visibility = Visibility.Collapsed;
                pnlInfoContainer.Visibility = Visibility.Collapsed;
                btnToggleInfo.Visibility = Visibility.Visible;
            }
        }
        private void AppliquerVisibiliteGrids()
        {
            infoExifXmp.Visibility = Visibility.Visible;
            if ((_currentMetadata.Titre != null) || txtXmpKeywords.Text != "Aucun mot clé")
            {
                if (_currentMetadata.Titre == null) txtXmpTitle.Text = "Pas de titre";
                infoTitleKeywords.Visibility = Visibility.Visible;
            }
            else
            {
                infoTitleKeywords.Visibility = Visibility.Collapsed;
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // Modification Titre et Mots-clés
        // ─────────────────────────────────────────────────────────────────────
        private List<string> _motsClesEnEdition = new List<string>();     // Copie de travail des mots-clés de la photo courante, modifiée pendant que la boite de dialogue est ouverte (appliquée à _currentMetadata.Keywords seulement à la fermeture)
        private void BtnTitreMotsCles_Click(object sender, RoutedEventArgs e)
        {
            // 🎯 On empêche le clic de remonter au Grid parent (infoExifXmp), qui replierait le panneau d'infos
            e.Handled = true;

            if (_currentMetadata == null) return;

            // Pré-remplissage du titre
            txtTitre.Text = _currentMetadata.Titre ?? "";

            // Copie de travail des mots-clés actuels (on ne touche pas directement à _currentMetadata.Keywords avant la fermeture)
            _motsClesEnEdition = _currentMetadata.Keywords != null
                ? new List<string>(_currentMetadata.Keywords)
                : new List<string>();
            RafraichirWrapMotsCles();

            txtNouveauMotCle.Text = "";

            MettreAJourBoutonsSuggestionsMotsCles();

            // 🎯 ON ALLUME LE FLOU DERRIÈRE, comme pour le panneau Options
            viewBlur.Radius = 15;

            // Indique qu'une boite de dialogue est ouverte (utile pour désactiver la spacemouse)
            _OptionOpen = true;

            // Empêche la touche Espace de fermer/toggler QuickLook tant que ce dialogue est ouvert
            // (sinon, taper un espace dans le titre ou un mot-clé peut fermer toute la fenêtre QuickLook)
            if (_context != null) _context.BlocageEspaceFerme = true;

            // Affiche la boite de dialogue
            gridTitreMotsCles.Visibility = Visibility.Visible;
            txtTitre.Focus();
        }
        private void BtnCloseTitreMotsCles_Click(object sender, RoutedEventArgs e)
        {
            // 🎯 ON ÉTEINT LE FLOU : Le panorama redevient instantanément net
            viewBlur.Radius = 0;

            _OptionOpen = false;

            // Redonne la possibilité de fermer QuickLook avec la touche Espace
            if (_context != null) _context.BlocageEspaceFerme = false;

            gridTitreMotsCles.Visibility = Visibility.Collapsed;

            if (_currentMetadata != null)
            {
                // Titre : chaîne vide considérée comme "pas de titre" (cohérent avec la lecture, qui laisse Titre à null si absent)
                string titreSaisi = txtTitre.Text?.Trim();
                _currentMetadata.Titre = string.IsNullOrEmpty(titreSaisi) ? null : titreSaisi;

                // Mots-clés : on applique la copie de travail
                _currentMetadata.Keywords = new List<string>(_motsClesEnEdition);

                // Sauvegarde temporisée via le flag _isMetaChanging
                _isMetaChanging = true;

                //// Sauvegarde immédiate dans le fichier (même pattern que SauvegardeMetadata pour Rating/Label)
                //if (!string.IsNullOrEmpty(_currentPanoPath))
                //{
                //    PanoMetadataService.WriteMetadata(_currentPanoPath, _currentMetadata);
                //}

                // Rafraîchissement de l'affichage du panneau d'infos (titre/mots-clés affichés au survol)
                UpdateDataPanelInfo();
                AppliquerVisibiliteGrids();
            }

            // Sauvegarde définitive des settings (dont les 6 derniers mots-clés)
            SaveSettings();
        }
        private void GridTitreMotsCles_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 🎯 On indique à WPF que l'événement s'arrête ici.
            // Le clic est "consommé" par le fond noir et ne traverse pas vers le panorama !
            e.Handled = true;
        }
        private void GridTitreMotsCles_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 🎯 On bloque aussi la molette de la souris pour empêcher le zoom en arrière-plan
            e.Handled = true;
        }
        // ── Puces des mots-clés déjà présents sur la photo (générées dynamiquement) ──
        private void RafraichirWrapMotsCles()
        {
            wrapMotsCles.Children.Clear();

            foreach (string motCle in _motsClesEnEdition)
            {
                wrapMotsCles.Children.Add(CreerPuceMotCle(motCle));
            }
        }
        private Border CreerPuceMotCle(string motCle)
        {
            var bouton = new Button
            {
                Content = "✕",
                Width = 16,
                Height = 16,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 9,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B3FFFFFF")),
                BorderThickness = new Thickness(0),
                Tag = motCle
            };
            bouton.Click += BtnSupprimerMotCle_Click;

            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = motCle,
                Foreground = Brushes.White,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(bouton);

            return new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF00AAFF")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Child = panel
            };
        }
        private void BtnSupprimerMotCle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button bouton && bouton.Tag is string motCle)
            {
                _motsClesEnEdition.Remove(motCle);
                RafraichirWrapMotsCles();
            }
        }
        // ── Ajout d'un nouveau mot-clé (TextBox + Entrée, ou bouton ➕) ──
        private void TxtNouveauMotCle_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AjouterMotCle(txtNouveauMotCle.Text);
                e.Handled = true;
            }
        }
        private void BtnAjouterMotCle_Click(object sender, RoutedEventArgs e)
        {
            AjouterMotCle(txtNouveauMotCle.Text);
        }
        private void AjouterMotCle(string motCle)
        {
            motCle = motCle?.Trim();
            if (string.IsNullOrEmpty(motCle)) return;

            // Pas de doublon (insensible à la casse, pour éviter "Plage" et "plage" en double)
            if (!_motsClesEnEdition.Any(m => string.Equals(m, motCle, StringComparison.OrdinalIgnoreCase)))
            {
                _motsClesEnEdition.Add(motCle);
                RafraichirWrapMotsCles();

                EnregistrerMotCleDansHistorique(motCle);
            }

            txtNouveauMotCle.Text = "";
            txtNouveauMotCle.Focus();
        }
        // ── 6 boutons "suggestions" : les derniers mots-clés utilisés via cette boite de dialogue ──
        private void MettreAJourBoutonsSuggestionsMotsCles()
        {
            var boutons = new[] { btnMotCleRecent1, btnMotCleRecent2, btnMotCleRecent3, btnMotCleRecent4, btnMotCleRecent5, btnMotCleRecent6 };

            for (int i = 0; i < boutons.Length; i++)
            {
                if (i < _DerniersMotsCles.Count)
                {
                    boutons[i].Content = _DerniersMotsCles[i];
                    boutons[i].Tag = _DerniersMotsCles[i];
                    boutons[i].IsEnabled = true;
                }
                else
                {
                    boutons[i].Content = "";
                    boutons[i].Tag = null;
                    boutons[i].IsEnabled = false;
                }
            }
        }
        private void BtnMotCleRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button bouton && bouton.Tag is string motCle)
            {
                AjouterMotCle(motCle);
            }
        }
        // ── Mémorisation des 6 derniers mots-clés utilisés (le plus récent en tête) ──
        private void EnregistrerMotCleDansHistorique(string motCle)
        {
            // On retire l'éventuelle occurrence existante (insensible à la casse) avant de la remettre en tête,
            // pour qu'un mot-clé réutilisé remonte en première position plutôt que d'être dupliqué.
            _DerniersMotsCles.RemoveAll(m => string.Equals(m, motCle, StringComparison.OrdinalIgnoreCase));
            _DerniersMotsCles.Insert(0, motCle);

            // On ne garde que les 6 derniers
            if (_DerniersMotsCles.Count > 6)
            {
                _DerniersMotsCles.RemoveRange(6, _DerniersMotsCles.Count - 6);
            }

            MettreAJourBoutonsSuggestionsMotsCles();
        }
        #endregion Information
        #region GPS
        // ─────────────────────────────────────────────────────────────────────
        // Panneau Carte GPS (WebView2 + Leaflet/OSM)
        // ─────────────────────────────────────────────────────────────────────
        // ─────────────────────── Evenements ──────────────────────────────────
        private void BtnToggleMap_Click(object sender, RoutedEventArgs e)
        {
            _isMapPanelVisible = !_isMapPanelVisible;   // bascule on/off

            if (_isMapPanelVisible && _context != null)
            {
                // Empêche la touche Espace de fermer/toggler QuickLook tant que ce dialogue est ouvert
                // (sinon, taper un espace dans le titre ou un mot-clé peut fermer toute la fenêtre QuickLook)
                _context.BlocageEspaceFerme = true;
            }
            else 
            {
                // Redonne la possibilité de fermer QuickLook avec la touche Espace
                _context.BlocageEspaceFerme = false;
            }

            AffichagePanneauMap();

            e.Handled = true;
        }
        private void SliderHeading_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            txtHeadingValue.Text = $"{(int)e.NewValue}°";

            if (_sliderHeadingMisAJourProgrammatique) return;
            if (_currentMetadata == null) return;

            _currentMetadata.PoseHeadingDegrees = e.NewValue;
            _isMetaChanging = true;

            _ = MettreAJourFovSurCarte();
        }
        private void ThumbResizeMap_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double nouvelleLargeur = pnlMapContainer.Width - e.HorizontalChange;
            double nouvelleHauteur = pnlMapContainer.Height - e.VerticalChange;

            double hauteurMax = CalculerHauteurMaxFenetre();
            double largeurMax = CalculerLargeurMaxAvantRating(margeDroiteFixe: 30);

            pnlMapContainer.Width = Math.Max(LargeurMinMap, Math.Min(largeurMax, nouvelleLargeur));
            pnlMapContainer.Height = Math.Max(HauteurMinMap, Math.Min(hauteurMax, nouvelleHauteur));

            PositionnerPanneauPositionInfo();

            e.Handled = true;
        }
        // ────────────────── Creation de la carte ─────────────────────────────
        private void AffichagePanneauMap()
        {
            if (_isMapPanelVisible)
            {
                btnToggleMap.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8000AAFF"));
                pnlMapContainer.Visibility = Visibility.Visible;
                pnlHeadingControl.Visibility = Visibility.Visible;
                pnlPositionInfo.Visibility = Visibility.Visible;
                pnlPositionInfo.Opacity = 1;

                PositionnerPanneauPositionInfo();

                _ = AssurerCarteInitialiseeAsync();
                AfficherPositionSurCarte(_currentMetadata?.GpsLatitude, _currentMetadata?.GpsLongitude);
                _ = MettreAJourFovSurCarte();
            }
            else
            {
                btnToggleMap.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73000000"));
                pnlMapContainer.Visibility = Visibility.Collapsed;
                pnlHeadingControl.Visibility = Visibility.Collapsed;
                pnlPositionInfo.Visibility = Visibility.Collapsed;
                pnlPositionInfo.Opacity = 0;
            }
        }
        private void WebViewMap_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                if (string.IsNullOrWhiteSpace(json)) return;

                // 🛠️ CORRECTIF ROBUSTESSE : Si le JS a utilisé JSON.stringify(), le message arrive 
                // sous forme de chaîne de texte JSON. On la désérialise une première fois pour avoir le JSON brut.
                if (json.StartsWith("\""))
                {
                    json = JsonConvert.DeserializeObject<string>(json);
                }

                // 🛠️ // Désérialisation principale en dictionnaire : On utilise <string, object> pour accepter les coordonnées numériques
                var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (msg == null) return;

                // On extrait l'action (gère à la fois la clé "action" ou la clé "type" de ton clic droit)
                string commande = null;
                if (msg.TryGetValue("action", out object act) && act != null) commande = act.ToString();
                else if (msg.TryGetValue("type", out object t) && t != null) commande = t.ToString();

                switch (commande)
                {
                    case "restore_focus":
                        // Forcer l'exécution sur le thread UI de WPF
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            // 1. Focus logique WPF
                            viewport3D.Focus();

                            // 2. Arrachement du focus natif Windows (Win32) à la WebView2
                            var window = Window.GetWindow(this);
                            if (window != null)
                            {
                                var helper = new WindowInteropHelper(window);
                                if (helper.Handle != IntPtr.Zero)
                                {
                                    SetFocus(helper.Handle);
                                }
                            }
                        });
                        break;

                    case "drag_map":
                        // Quand on fait un "Drag" sur la carte

                        pnlPositionInfo.Visibility = Visibility.Collapsed;
                        pnlPositionInfo.Opacity = 0;
                        break;

                    case "contextmenu":
                        // Code du clic droit
                        double lat = Convert.ToDouble(msg["lat"]);
                        double lon = Convert.ToDouble(msg["lon"]);
                        _dernierClicZoom = Convert.ToInt32(msg["zoom"]);
                        int x = Convert.ToInt32(msg["containerX"]);
                        int y = Convert.ToInt32(msg["containerY"]);
                        _isContextMenu = true;

                        AfficherMenuContextuelCarte(lat, lon, x, y);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur WebView Message: {ex.Message}");
            }
        }
        private async Task AssurerCarteInitialiseeAsync()
        {
            if (_isMapWebViewReady) return;

            try
            {
                await webViewMap.EnsureCoreWebView2Async(null);

                // On désactive le menu contextuel natif de Chromium (Enregistrer sous, Imprimer, Inspecter, ...)
                // pour le remplacer par notre propre ContextMenu WPF.
                webViewMap.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webViewMap.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webViewMap.CoreWebView2.WebMessageReceived += WebViewMap_WebMessageReceived;

                string dossierLeaflet = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "leaflet");

                if (dossierLeaflet == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Carte GPS] Dossier Leaflet introuvable. " +
                        "Vérifiez que le dossier 'leaflet' (leaflet.js/leaflet.css/images) est bien copié " +
                        "dans le répertoire de sortie du build (Copy to Output Directory).");
                    return;
                }

                webViewMap.CoreWebView2.SetVirtualHostNameToFolderMapping("pano360-app.local", dossierLeaflet, CoreWebView2HostResourceAccessKind.Allow);

                webViewMap.CoreWebView2.Navigate("https://pano360-app.local/carte.html");

                // On attend la fin du chargement de la page avant de pouvoir appeler du JS (ex: déplacer le marqueur)
                void OnNavCompleted(object s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    webViewMap.CoreWebView2.NavigationCompleted -= OnNavCompleted;
                    _isMapWebViewReady = true;

                    btnToggleMap.Visibility = Visibility.Visible;
                    var sbIn = (Storyboard)btnToggleMap.Resources["FadeInbtnToggleMap"];
                    sbIn.Begin();

                    if (_pendingMapLat.HasValue && _pendingMapLon.HasValue)
                    {
                        System.Diagnostics.Debug.WriteLine($"AssurerCarteInitialiseeAsync (_zoomPinMap) : {_zoomPinMap}");
                        _ = DeplacerMarqueurAsync(_pendingMapLat.Value, _pendingMapLon.Value, _zoomPinMap);
                        _pendingMapLat = null;
                        _pendingMapLon = null;
                    }
                }
                webViewMap.CoreWebView2.NavigationCompleted += OnNavCompleted;
            }
            catch (Exception ex)
            {
                // Cas typique : WebView2 Runtime non installé sur la machine, ou package NuGet manquant à l'exécution
                System.Diagnostics.Debug.WriteLine($"Erreur d'initialisation WebView2 (carte GPS) : {ex.Message}");
            }
        }
        private void DefinirPositionGpsPhoto(double lat, double lon)
        {
            // Met à jour les coordonnées GPS du panorama en cours et marque les métadonnées comme
            // modifiées : la sauvegarde effective est différée et gérée par le mécanisme existant
            // (_isMetaChanging + SauvegardeMeta()).
            if (_currentMetadata == null) return;

            _currentMetadata.GpsLatitude = lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _currentMetadata.GpsLongitude = lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

            _isMetaChanging = true;      // Indique qu'il est nécessaire de sauvegarder

            // On rafraîchit immédiatement l'affichage (marqueur + sortie de l'état "pas de GPS" si besoin)
            AfficherPositionSurCarte(_currentMetadata.GpsLatitude, _currentMetadata.GpsLongitude);

            System.Diagnostics.Debug.WriteLine($"Nouvelle position GPS demandée : {_currentMetadata.GpsLatitude}, {_currentMetadata.GpsLongitude}");
        }
        private void AfficherPositionSurCarte(string latStr, string lonStr)
        {
            // Point d'entrée unique pour mettre à jour la carte : gère à la fois le cas "pas de GPS"
            // et la mise à jour du marqueur (immédiate si la WebView est prête, sinon mise en attente).
            //
            // Prend directement les string telles que stockées dans PanoMetadata.GpsLatitude/GpsLongitude
            // (format "F6" invariant culture, ex: "49.598494"), le parsing est fait ici une seule fois.
            bool latOk = TryParseGps(latStr, out double lat);
            bool lonOk = TryParseGps(lonStr, out double lon);
            bool aDesCoordonnees = latOk && lonOk;

            webViewMap.Visibility = aDesCoordonnees ? Visibility.Visible : Visibility.Collapsed;
            bdNoGps.Visibility = aDesCoordonnees ? Visibility.Collapsed : Visibility.Visible;

            if (!aDesCoordonnees)
            {
                MettreAJourLargeurPanneauPositionInfo("Localisation inconnue");
                _dernierePositionGeocodee = null;
                return;
            }

            if (_isMapWebViewReady)
            {
                System.Diagnostics.Debug.WriteLine($"AfficherPositionSurCarte (_isContextMenu) : {_isContextMenu}");
                if (_isContextMenu) 
                {
                    _ = DeplacerMarqueurAsync(lat, lon, _dernierClicZoom);
                    _currentMetadata.MapZoomLevel = _dernierClicZoom;
                    _zoomPinMap = _dernierClicZoom;
                    _isMetaChanging = true;      // Indique qu'il est nécessaire de sauvegarder
                }
                else 
                {
                    _ = DeplacerMarqueurAsync(lat, lon, _zoomPinMap);
                }
                _isContextMenu = false;
            }
            else
            {
                // La WebView2/Leaflet n'a pas encore fini de s'initialiser : on mémorise la position
                // pour l'appliquer dès que NavigationCompleted se déclenche.
                _pendingMapLat = lat;
                _pendingMapLon = lon;
            }

            _ = MettreAJourLocalisationTexteAsync(lat, lon);
        }
        private async Task MettreAJourFovSurCarte()
        // Calcule la direction absolue du centre du champ de vision (cap compass + rotation
        // actuelle de la vue dans le panorama) et met à jour le triangle FOV sur la carte Leaflet.
        // Ne fait rien si la WebView n'est pas prête, si le panneau est fermé, ou s'il n'y a pas de GPS.
        {
            if (!_isMapWebViewReady) return;
            if (!_isMapPanelVisible) return;
            if (_currentMetadata == null) return;
            if (!TryParseGps(_currentMetadata.GpsLatitude, out double lat)) return;
            if (!TryParseGps(_currentMetadata.GpsLongitude, out double lon)) return;

            // Cap de référence au moment de la capture (0=Nord si absent, voir choix produit).
            double headingReference = _currentMetadata.PoseHeadingDegrees ?? 0.0;

            // Rotation actuelle de la vue dans le panorama (lacet), à combiner avec le cap de référence.
            double angleVueActuelle = double.IsNaN(_targetHorizontalAngle) ? _horizontalRotation.Angle : _targetHorizontalAngle;

            double directionAbsolue = (headingReference + angleVueActuelle + OffsetHeadingSphereVersGPano) % 360.0;
            if (directionAbsolue < 0) directionAbsolue += 360.0;

            // FOV réel de la caméra 3D : reflète le zoom/dézoom actuel de la visionneuse.
            double fovDeg = _camera.FieldOfView;

            const double rayonPixels = 100;  //Modifie la taille du triangle pour visualiser la FOV sur la carte

            string script = $"setFovTriangle({lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{directionAbsolue.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{fovDeg.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {rayonPixels});";

            try
            {
                await webViewMap.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur lors de la mise à jour du triangle FOV : {ex.Message}");
            }
        }
        private async Task DeplacerMarqueurAsync(double lat, double lon, int zoom)
        {
            try
            {
                string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // setPosition() est défini dans le HTML/JS ci-dessous (ConstruireHtmlCarteLeaflet) :
                // il déplace le marqueur et recentre la carte sans tout recharger.
                await webViewMap.CoreWebView2.ExecuteScriptAsync($"setPosition({latStr}, {lonStr}, {zoom});");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur lors de la mise à jour du marqueur GPS : {ex.Message}");
            }
        }
        // ─────────────── reverse GPS ➡️ pays, ville, ... ─────────────────────
        private async Task MettreAJourLocalisationTexteAsync(double lat, double lon)
        {
            // Résout pays + ville via géocodage inverse (Nominatim/OSM) et met à jour txtPositionInfo.
            // Repositionne le panneau après coup car son contenu (donc sa largeur) change.
            string clePosition = lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                + "," + lon.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

            if (clePosition == _dernierePositionGeocodee) return;   // Évite un appel réseau si la position n'a pas vraiment changé

            // Localisation déjà connue en métadonnées (lue depuis le fichier à l'ouverture, ou écrite lors
            // d'un précédent géocodage pour cette même photo) ? On l'affiche directement sans appel réseau.
            // Un appel forcé (menu contextuel, cf. RechercherEtEnregistrerLocalisationAsync) contourne ce test.
            if (_currentMetadata != null && _currentMetadata.ALocalisationConnue)
            {
                MettreAJourLargeurPanneauPositionInfo(FormaterTexteLocalisation(_currentMetadata));
                PositionnerPanneauPositionInfo();
                _dernierePositionGeocodee = clePosition;

                System.Diagnostics.Debug.WriteLine("[Géocodage] Localisation déjà connue en métadonnées, pas d'appel Nominatim.");
                return;
            }

            await RechercherEtEnregistrerLocalisationAsync(lat, lon, clePosition);
        }
        private async Task RechercherEtEnregistrerLocalisationAsync(double lat, double lon, string clePosition = null)
        {
            // ─────────────────────────────────────────────────────────────────────
            // Interroge Nominatim (géocodage inverse) et persiste systématiquement le résultat dans
            // _currentMetadata (sauvegarde différée via _isMetaChanging, comme le reste des métadonnées).
            // Contourne tout cache existant : appelée directement, elle écrase toujours la localisation
            // précédente. Utilisable depuis MettreAJourLocalisationTexteAsync (rien en cache) ou directement
            // depuis un menu contextuel "Rechercher la localisation" pour forcer un rafraîchissement.
            // clePosition est optionnel : si non fourni, il est recalculé à partir de lat/lon.
            // ─────────────────────────────────────────────────────────────────────
            clePosition = clePosition ?? (lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                + "," + lon.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

            _ctsGeocodage?.Cancel();
            var cts = new CancellationTokenSource();
            _ctsGeocodage = cts;

            MettreAJourLargeurPanneauPositionInfo("Localisation…");
            PositionnerPanneauPositionInfo();

            try
            {
                // Le niveau de zoom est important pour un zoom=10, on obtient la ville et le pays, mais pas l'adresse exacte.
                // Pour un zoom=18, on obtient l'adresse exacte (rue + numéro) si disponible.
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={0}&lon={1}&zoom=18&accept-language=fr",
                    lat, lon);

                using (var requete = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    // User-Agent personnalisé requis par la politique d'usage de Nominatim
                    // (https://operations.osmfoundation.org/policies/nominatim/).
                    requete.Headers.UserAgent.ParseAdd("Pano360Panel/1.0 (QuickLook plugin)");

                    using (var reponse = await _httpClientGeocodage.SendAsync(requete, cts.Token))
                    {
                        reponse.EnsureSuccessStatusCode();
                        string json = await reponse.Content.ReadAsStringAsync();

                        if (cts.IsCancellationRequested) return;

                        var resultat = JsonConvert.DeserializeObject<NominatimReverseResult>(json);
                        string ville = resultat?.Address?.City ?? resultat?.Address?.Town ?? resultat?.Address?.Village ?? resultat?.Address?.Municipality;
                        string region = resultat?.Address?.State;
                        string comte = resultat?.Address?.Comte;
                        string pays = resultat?.Address?.Country;
                        string rue = resultat?.Address?.Road;
                        string numero = resultat?.Address?.HouseNumber;
                        string codePostal = resultat?.Address?.PostCode;
                        string CountryCode = resultat?.Address?.CountryCode;

                        System.Diagnostics.Debug.WriteLine($"[Géocodage] Reverse gps : {numero} - {rue} - {ville} - {codePostal} - {region} - {comte} - {pays} - {CountryCode}");

                        // Persistance dans les métadonnées de la photo courante (sauvegarde différée, comme le
                        // reste : GPS, heading... cf. _isMetaChanging + SauvegardeMeta()).
                        if (_currentMetadata != null)
                        {
                            _currentMetadata.LocHouseNumber = numero;
                            _currentMetadata.LocRoad = rue;
                            _currentMetadata.LocCity = ville;
                            _currentMetadata.LocPostCode = codePostal;
                            _currentMetadata.LocCounty = comte;
                            _currentMetadata.LocState = region;
                            _currentMetadata.LocCountry = pays;
                            _currentMetadata.LocCountryCode = CountryCode;

                            _isMetaChanging = true;      // Indique qu'il est nécessaire de sauvegarder
                        }

                        string texte;
                        if (!string.IsNullOrEmpty(ville) && !string.IsNullOrEmpty(pays))
                            texte = $"{ville}, {pays}";
                        else if (!string.IsNullOrEmpty(pays))
                            texte = pays;
                        else
                            texte = "Localisation inconnue";

                        MettreAJourLargeurPanneauPositionInfo(texte);
                        _dernierePositionGeocodee = clePosition;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Requête annulée car une position plus récente est arrivée : on ne touche pas au texte.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Géocodage] Erreur Nominatim : {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[Géocodage] InnerException : {ex.InnerException?.GetType().Name} - {ex.InnerException?.Message}");
                MettreAJourLargeurPanneauPositionInfo("Localisation indisponible");
            }
            finally
            {
                if (ReferenceEquals(_ctsGeocodage, cts))
                    PositionnerPanneauPositionInfo();
            }
        }
        private void PositionnerPanneauPositionInfo()
        {
            // Repositionne pnlPositionInfo pour qu'il reste centré horizontalement au-dessus de
            // pnlMapContainer, et colle juste au-dessus de son bord supérieur.
            //
            // N'utilise ni Measure()/DesiredSize ni TranslatePoint : ces deux approches forcent un passe de
            // layout qui entre en conflit avec le HwndHost du WebView2 pendant ThumbResizeMap_DragDelta
            // (clignotement/décalage). La largeur réelle du panneau (_largeurPanneauPositionInfo) est calculée
            // à part, via FormattedText, uniquement quand le texte change (cf CalculerLargeurTexteLocalisation),
            // PAS à chaque repositionnement. Ici on ne fait plus que de l'arithmétique pure.

            const double margeDroiteCarte = 30;   // Doit correspondre au Margin droit de pnlMapContainer
            const double margeBasCarte = 90;      // Doit correspondre au Margin bas de pnlMapContainer
            const double espacementVertical = 8;  // Espace entre le bas de pnlPositionInfo et le haut de la carte
            const double hauteurPanneauInfo = 33; // Hauteur réelle approximative (Padding 6+6 vertical + texte ~21px de haut)

            double bordDroitCarte = this.ActualWidth - margeDroiteCarte;
            double bordGaucheCarte = bordDroitCarte - pnlMapContainer.Width;
            double centreCarteX = bordGaucheCarte + (pnlMapContainer.Width / 2);

            double bordHautCarte = this.ActualHeight - margeBasCarte - pnlMapContainer.Height;

            double marginLeft = centreCarteX - (_largeurPanneauPositionInfo / 2);
            double marginTop = bordHautCarte - hauteurPanneauInfo - espacementVertical;

            pnlPositionInfo.Width = _largeurPanneauPositionInfo;
            pnlPositionInfo.Margin = new Thickness(marginLeft, marginTop, 0, 0);
        }
        private void MettreAJourLargeurPanneauPositionInfo(string texte)
        {
            // Calcule la largeur réelle nécessaire pour afficher `texte` avec la police/taille de txtPositionInfo,
            // via FormattedText : contrairement à Measure()/DesiredSize, cela ne touche pas à l'arbre visuel et
            // ne déclenche aucun passe de layout (donc aucun risque de conflit avec le HwndHost du WebView2).
            // Le résultat est mis en cache dans _largeurPanneauPositionInfo, et le panneau est repositionné
            // immédiatement avec la nouvelle largeur.

            txtPositionInfo.Text = texte;

            const double largeurMax = 320;        // Largeur max avant troncature du texte (au-delà, CharacterEllipsis prendrait le relais visuellement)
            const double largeurMin = 130;        // Largeur min pour ne pas avoir un panneau trop étroit avec un texte court ("Paris")
            const double largeurIcone = 21;       // Marge gauche, Largeur de l'emoji 📍 + son Margin droit 
            const double largeurPadding = 38;     // Marge droite, Padding="12,6" du Border : 12 de chaque côté (24 au début, mais trop court)

            var typeface = new Typeface(txtPositionInfo.FontFamily, txtPositionInfo.FontStyle, txtPositionInfo.FontWeight, txtPositionInfo.FontStretch);

            var formattedText = new FormattedText(
                texte,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                txtPositionInfo.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double largeurTexte = formattedText.WidthIncludingTrailingWhitespace;
            double largeurSouhaitee = largeurTexte + largeurIcone + largeurPadding;

            _largeurPanneauPositionInfo = Math.Max(largeurMin, Math.Min(largeurMax, largeurSouhaitee));

            PositionnerPanneauPositionInfo();
        }
        // ──────────────────── Menu clic droit ────────────────────────────────
        private void AfficherMenuContextuelCarte(double lat, double lon, double containerX, double containerY)
        // Mémorise les coordonnées du clic droit, puis ouvre le ContextMenu déclaré en XAML
        // (ressource "MenuContextuelCarte"), positionné exactement à l'endroit du clic.
        {
            _dernierClicCarteLat = lat;
            _dernierClicCarteLon = lon;

            var menu = (ContextMenu)FindResource("MenuContextuelCarte");

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
            menu.PlacementTarget = webViewMap;
            menu.HorizontalOffset = containerX;
            menu.VerticalOffset = containerY;
            menu.IsOpen = true;
        }
        private async void MenuInfoPosition_Click(object sender, RoutedEventArgs e)
        {
            txtPositionInfo.Text = await RechercherNomLieuAsync(_dernierClicCarteLat, _dernierClicCarteLon);

            pnlPositionInfo.Visibility = Visibility.Visible;
            pnlPositionInfo.Opacity = 1;
        }
        private void MenuDefinirPosition_Click(object sender, RoutedEventArgs e)
        {
            // Nouvelle position GPS : l'ancienne localisation textuelle (si présente en métadonnées) ne
            // correspond plus à rien. On vide le cache avant l'appel ci-dessous, pour que la chaîne
            // DefinirPositionGpsPhoto → AfficherPositionSurCarte → MettreAJourLocalisationTexteAsync
            // ne réutilise pas une localisation obsolète et relance bien une recherche Nominatim.
            if (_currentMetadata != null)
            {
                _currentMetadata.LocHouseNumber = null;
                _currentMetadata.LocRoad = null;
                _currentMetadata.LocCity = null;
                _currentMetadata.LocPostCode = null;
                _currentMetadata.LocCounty = null;
                _currentMetadata.LocState = null;
                _currentMetadata.LocCountry = null;
                _currentMetadata.LocCountryCode = null;
            }
            _dernierePositionGeocodee = null;

            DefinirPositionGpsPhoto(_dernierClicCarteLat, _dernierClicCarteLon);

            pnlPositionInfo.Visibility = Visibility.Visible;
            pnlPositionInfo.Opacity = 1;
        }
        private void MenuRecentrerSurPin_Click(object sender, RoutedEventArgs e)
        {
            pnlPositionInfo.Visibility = Visibility.Visible;
            pnlPositionInfo.Opacity = 1;

            _ = RecentrerCarteSurMarqueurAsync();
        }
        private void MenuCentrerPanoramaPrecedent_Click(object sender, RoutedEventArgs e) { /* ToDo: */ }
        private void MenuPositionFavorite1_Click(object sender, RoutedEventArgs e)
        {
            pnlPositionInfo.Visibility = Visibility.Collapsed;
            pnlPositionInfo.Opacity = 0;

            _ = GoToPosition(_GpsMemoire1Latitude, _GpsMemoire1Longitude, _GpsMemoire1Zoom);
        }
        private void MenuPositionFavorite2_Click(object sender, RoutedEventArgs e)
        {
            pnlPositionInfo.Visibility = Visibility.Collapsed;
            pnlPositionInfo.Opacity = 0;

            _ = GoToPosition(_GpsMemoire2Latitude, _GpsMemoire2Longitude, _GpsMemoire2Zoom);
        }
        private void MenuPositionFavorite3_Click(object sender, RoutedEventArgs e)
        {
            pnlPositionInfo.Visibility = Visibility.Collapsed;
            pnlPositionInfo.Opacity = 0;

            _ = GoToPosition(_GpsMemoire3Latitude, _GpsMemoire3Longitude, _GpsMemoire3Zoom);
        }
        private async void MenuSavePositionFavorite1_Click(object sender, RoutedEventArgs e)
        {
            _GpsMemoire1Latitude = _dernierClicCarteLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _GpsMemoire1Longitude = _dernierClicCarteLon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _GpsMemoire1Zoom = _dernierClicZoom;

            _GpsMemoire1Nom = "Recherche…";
            MettreAJourLibellesFavoris();

            _GpsMemoire1Nom = await RechercherNomLieuAsync(_dernierClicCarteLat, _dernierClicCarteLon);
            MettreAJourLibellesFavoris();

            SaveSettings();
        }
        private async void MenuSavePositionFavorite2_Click(object sender, RoutedEventArgs e)
        {
            _GpsMemoire2Latitude = _dernierClicCarteLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _GpsMemoire2Longitude = _dernierClicCarteLon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _GpsMemoire2Zoom = _dernierClicZoom;

            _GpsMemoire2Nom = "Recherche…";
            MettreAJourLibellesFavoris();

            _GpsMemoire2Nom = await RechercherNomLieuAsync(_dernierClicCarteLat, _dernierClicCarteLon);
            MettreAJourLibellesFavoris();

            SaveSettings();
        }
        private async void MenuSavePositionFavorite3_Click(object sender, RoutedEventArgs e)
        {
            _GpsMemoire3Latitude = _dernierClicCarteLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _GpsMemoire3Longitude = _dernierClicCarteLon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _GpsMemoire3Zoom = _dernierClicZoom;

            _GpsMemoire3Nom = "Recherche…";
            MettreAJourLibellesFavoris();

            _GpsMemoire3Nom = await RechercherNomLieuAsync(_dernierClicCarteLat, _dernierClicCarteLon);
            MettreAJourLibellesFavoris();

            SaveSettings();
        }
        private void MenuSauvegardeVueEtMap_Click(object sender, RoutedEventArgs e)
        {
            _currentMetadata.MapZoomLevel = _dernierClicZoom;
            _zoomPinMap = _dernierClicZoom;

            _currentMetadata.HomeHorizontalSphere = _horizontalRotation.Angle; 
            _currentMetadata.HomeVerticalSphere = _verticalRotation.Angle;
            _currentMetadata.HomeFovSphere = _camera.FieldOfView;

            _isMetaChanging = true;      // Indique qu'il est nécessaire de sauvegarder
            SauvegardeMeta();
        }
        #endregion GPS
        #region Helpers
        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        // ────────────────────────── Math ─────────────────────────────────────
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
        private double ComputeAutoCloseTimeRemaining()
        {
            // ─────────────────────────────────────────────────────────────────────
            // Calcul du temps restant avant AutoClose (3 phases)
            // ─────────────────────────────────────────────────────────────────────

            double vMax = 0;
            if (_autoRotState == AutoRotationState.Lent) vMax = 360.0 / AutoRotateSlowSeconds;
            else if (_autoRotState == AutoRotationState.Normal) vMax = 360.0 / AutoRotateNormalSeconds;
            else if (_autoRotState == AutoRotationState.Rapide) vMax = 360.0 / AutoRotateFastSeconds;

            if (vMax <= 0) return 0;

            // ── Phase de freinage final : on ne se base plus sur la géométrie ──
            // On estime directement depuis la vitesse instantanée qui décroît
            if (_isAutoClosingPhase)
            {
                double v0 = Math.Max(_currentRotationSpeed, 0);
                double decelEff = _isMouseInertia
                    ? DecelerationRate + 0.02 * v0 * v0 * 0.33
                    : DecelerationRate;

                // t = v / a  (freinage linéaire depuis la vitesse courante)
                return decelEff > 0 ? v0 / decelEff : 0;
            }

            // ── Phases normales : accélération résiduelle + croisière + freinage futur ──
            double v_current = _currentRotationSpeed;

            // Phase 1 — accélération résiduelle
            double tAccel = 0, θAccel = 0;
            if (v_current < vMax)
            {
                tAccel = (vMax - v_current) / AccelerationRate;
                θAccel = (vMax * vMax - v_current * v_current) / (2.0 * AccelerationRate);
            }

            // Phase 3 — décélération future (calculée à vMax, pas encore commencée)
            double decelEffFuture = _isMouseInertia
                ? DecelerationRate + 0.02 * vMax * vMax * 0.33
                : DecelerationRate;
            double tDecel = vMax / decelEffFuture;
            double θDecel = vMax * tDecel / 2.0;

            // Phase 2 — croisière
            double angleParcouru = (_horizontalRotation.Angle - _autoCloseStartAngle + 360.0) % 360.0;
            double angleRestantTotal = 360.0 - angleParcouru;
            double θConstante = angleRestantTotal - θAccel - θDecel;
            double tConstante = θConstante > 0 ? θConstante / vMax : 0;

            return tAccel + tConstante + tDecel;
        }
        // ────────────────────────── GPS ──────────────────────────────────────
        private class NominatimReverseResult
        {
            // ── Modèles minimalistes pour désérialiser la réponse JSON de Nominatim (reverse geocoding) ────
            [JsonProperty("address")]
            public NominatimAddress Address { get; set; }
        }
        private async Task RecentrerCarteSurMarqueurAsync()
        {
            if (!_isMapWebViewReady) return;

            try
            {
                await webViewMap.CoreWebView2.ExecuteScriptAsync("recentrerSurMarqueur();");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur lors du recentrage sur le marqueur : {ex.Message}");
            }
        }
        private async Task GoToPosition(string latitude, string longitide, int zoom)
        {
            // On vérifie que la WebView est bien initialisée et prête
            if (_isMapWebViewReady && webViewMap?.CoreWebView2 != null)
            {
                // Extraction et parsing des coordonnées stockées dans la mémoire 1
                if (TryParseGps(latitude, out double lat) &&
                    TryParseGps(longitide, out double lon))
                {
                    // Conversion stricte au format international avec un point "." décimal
                    string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    // Construction de la commande JavaScript à exécuter
                    string script = $"GotoView({latStr}, {lonStr}, {zoom});";

                    try
                    {
                        // Envoi de la commande à la carte Leaflet
                        await webViewMap.CoreWebView2.ExecuteScriptAsync(script);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Erreur lors de l'appel à GotoView : {ex.Message}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Les coordonnées de la position favorite 1 sont invalides ou vides.");
                }
            }
        }
        private double CalculerHauteurMaxFenetre()
        {
            const double margeBasFixe = 70;   // doit correspondre au Margin bas de pnlMapContainer
            const double margeHautMin = 70 + HauteurPanneauPositionInfo;  // + place réservée pour pnlPositionInfo au-dessus de la carte
            return this.ActualHeight - margeBasFixe - margeHautMin;
        }
        private double CalculerLargeurMaxAvantRating(double margeDroiteFixe)
        // panelRating est centré horizontalement et peut être masqué (Visibility.Collapsed), donc on ne
        // peut pas se fier à ActualWidth à cet instant : on le mesure explicitement même caché, une fois,
        // pour connaître sa largeur réelle (texte + boutons + padding) indépendamment de sa visibilité actuelle.
        {
            panelRating.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double largeurRating = panelRating.DesiredSize.Width;

            double bordDroitRating = (this.ActualWidth / 2) + (largeurRating / 2);

            const double margeSecurite = 20;

            return this.ActualWidth - bordDroitRating - margeDroiteFixe - margeSecurite;
        }
        private void ContraindreTailleMap()
        {
            // Calcule les limites actuelles (haut de fenêtre / barre de notation) et ramène Width/Height
            // de pnlMapContainer dans ces bornes si nécessaire. Appelée à la fois pendant le drag de
            // redimensionnement et lors d'un changement de taille de la fenêtre (plein écran, etc.).
            double hauteurMax = CalculerHauteurMaxFenetre();
            double largeurMax = CalculerLargeurMaxAvantRating(margeDroiteFixe: 30);

            if (pnlMapContainer.Width > largeurMax)
                pnlMapContainer.Width = Math.Max(LargeurMinMap, largeurMax);

            if (pnlMapContainer.Height > hauteurMax)
                pnlMapContainer.Height = Math.Max(HauteurMinMap, hauteurMax);

            PositionnerPanneauPositionInfo();
        }
        private static bool TryParseGps(string value, out double result)
        {
            // PanoMetadataService formate toujours GpsLatitude/GpsLongitude en "F6" invariant culture,
            // mais on reste tolérant (NumberStyles.Float) au cas où la source évolue.
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        private void Pano360Panel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Se déclenche notamment lors du basculement plein écran (clic droit) ou si la fenêtre QuickLook
            // est redimensionnée. Si le panneau carte est ouvert et devient trop grand pour la nouvelle taille
            // disponible, on le re-contraint pour que la poignée de redimensionnement reste toujours accessible.
            if (!_isMapPanelVisible) return;

            ContraindreTailleMap();
        }
        private static string FormaterTexteLocalisation(PanoMetadata data)
        {
            // Formate le texte affiché dans le panneau à partir d'une localisation déjà connue en métadonnées
            // (même règle d'affichage que la recherche Nominatim : "Ville, Pays" / "Pays" / "Localisation inconnue").
            if (!string.IsNullOrEmpty(data.LocCity) && !string.IsNullOrEmpty(data.LocCountry))
                return $"{data.LocCity}, {data.LocCountry}";
            if (!string.IsNullOrEmpty(data.LocCountry))
                return data.LocCountry;
            if (!string.IsNullOrEmpty(data.LocCity))
                return data.LocCity;
            return "Localisation inconnue";
        }
        private async Task<string> RechercherNomLieuAsync(double lat, double lon)
        {
            // ─────────────────────────────────────────────────────────────────────
            // Géocodage inverse dédié aux 3 positions favorites (menu contextuel carte) : indépendant de
            // _currentMetadata et de _dernierePositionGeocodee (qui pilotent l'affichage de la photo courante).
            // ─────────────────────────────────────────────────────────────────────
            try
            {
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={0}&lon={1}&zoom=10&accept-language=fr",
                    lat, lon);

                using (var requete = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    requete.Headers.UserAgent.ParseAdd("Pano360Panel/1.0 (QuickLook plugin)");

                    using (var reponse = await _httpClientGeocodage.SendAsync(requete))
                    {
                        reponse.EnsureSuccessStatusCode();
                        string json = await reponse.Content.ReadAsStringAsync();

                        var resultat = JsonConvert.DeserializeObject<NominatimReverseResult>(json);
                        string ville = resultat?.Address?.City ?? resultat?.Address?.Town ?? resultat?.Address?.Village ?? resultat?.Address?.Municipality;
                        string pays = resultat?.Address?.Country;

                        if (!string.IsNullOrEmpty(ville) && !string.IsNullOrEmpty(pays))
                            return $"{ville}, {pays}";
                        if (!string.IsNullOrEmpty(pays))
                            return pays;
                        return "Position favorite";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Géocodage] Erreur Nominatim (favori) : {ex.GetType().Name} - {ex.Message}");
                return "Position favorite";
            }
        }
        private void MettreAJourLibellesFavoris()
        {
            // Met à jour le Header des 3 MenuItem favoris du menu contextuel carte avec le nom de lieu mémorisé,
            // pour que l'utilisateur voie "Paris, France" plutôt que "Aller au favoris 1".
            var menu = (ContextMenu)FindResource("MenuContextuelCarte");

            MettreAJourHeaderFavori(menu, "Favori1", _GpsMemoire1Nom);
            MettreAJourHeaderFavori(menu, "Favori2", _GpsMemoire2Nom);
            MettreAJourHeaderFavori(menu, "Favori3", _GpsMemoire3Nom);
        }
        private void MettreAJourHeaderFavori(ItemsControl menu, string tag, string nomLieu)
        {
            // Retrouve, par son Tag (identifiant stable posé en XAML, indépendant de l'ordre des MenuItem
            // et du nom du handler Click), le MenuItem "Aller au favoris X" correspondant et met à jour son
            // Header. Nécessaire car x:Name sur un élément déclaré statiquement dans une ressource
            // (ContextMenu en Window.Resources) n'est pas accessible via FindName : on parcourt donc Items.
            foreach (var obj in menu.Items)
            {
                if (obj is MenuItem item && (string)item.Tag == tag)
                {
                    item.Header = "⭐  " + nomLieu;
                    return;
                }
            }
        }
        // ──────────────────────── Affichage ──────────────────────────────────
        private void ToggleFullscreen()
        {
            if (_OptionOpen) return;

            var window = Window.GetWindow(this);
            if (window == null) return;

            var method = window.GetType().GetMethod("ToggleFullscreen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

            method?.Invoke(window, null);
        }
        private async Task ShowMessageAndNavigateAsync(int direction)
        {
            txtInfoPopup.Text = direction > 0
                ? "Panorama suivant  ▶"
                : "◀  Panorama précédent";

            (infoPopup.Resources["StoryboardShowInfoRapide"] as Storyboard)?.Begin(infoPopup);

            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            await Task.Delay(350);

            NavigateToAdjacentPano(direction);
        }
        private void ShowRatingPanelZoomEtOpacity()
        {
            // Remplace la méthode : panelRating.Visibility = Visible
            panelRating.Visibility = Visibility.Visible;
            var sb = (Storyboard)panelRating.Resources["FadeInEtZoomRating"];
            sb.Begin();
        }
        private void HideRatingPanelZoomEtOpacity()
        {
            // Remplace la méthode : panelRating.Visibility = Collapsed
            var sb = (Storyboard)panelRating.Resources["FadeOutEtZoomRating"];
            sb.Begin();
            // Le Collapsed est appliqué dans FadeOutRating_Completed
        }
        private void FadeOutZoomEtOpacityRating_Completed(object sender, EventArgs e)
        {
            // Handler déclenché à la fin du FadeOut
            panelRating.Visibility = Visibility.Collapsed;

            // Remet le scale à 1 pour le prochain FadeIn
            panelRatingScale.ScaleX = 1.0;
            panelRatingScale.ScaleY = 1.0;
            panelRatingTranslate.Y = 0; // reset pour le prochain FadeIn
        }
        private void MiseAJourFovUI()
        {
            // Permet la mise à jour de la fov au démarrage de l'application
            // mais également au changement de panorama
            // Mise à jour de l'interface (UI) et de la caméra
            if (_homeFov == 0)
            {
                _camera.FieldOfView = _OptionFov;
            }
            else
            {
                _camera.FieldOfView = _homeFov;
            }
            _targetFov = _camera.FieldOfView;
            txtFov.Text = string.Format("(FOV: {0:F0}°)", _camera.FieldOfView);
        }
        #endregion Helpers
        // ─────────────────────────────────────────────────────────────────────
        // Dispose — nettoyage des ressources
        // ─────────────────────────────────────────────────────────────────────
        public void Dispose()
        {
            // 1. Sauvegarde finale du panorama en cours avant la fermeture complète
            if (_isMetaChanging) SauvegardeMeta();

            // 2. ARRÊTER LES PROCESSUS ACTIFS ET ÉVÉNEMENTS GLOBAUX
            // On coupe la boucle de rendu en priorité pour geler l'affichage
            CompositionTarget.Rendering -= OnRendering;

            // On arrête le timer de préchargement pour éviter qu'il ne se lance pendant la destruction
            if (_idleTimer != null)
            {
                _idleTimer.Stop();
                _idleTimer.Tick -= OnIdleTimerTick;
            }

            // On se désabonne des événements de la fenêtre parente
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewKeyDown -= OnWindowKeyDown;
                window.PreviewKeyUp -= OnWindowKeyUp;
                this.SizeChanged -= Pano360Panel_SizeChanged;
            }

            // 3. DÉCONNECTER LE MATÉRIEL
            // Libération des ressources de la souris 3D (COM/USB)
            DeconnecterSpaceMouse();

            // 4. NETTOYER LES DONNÉES LOURDES ET L'INTERFACE
            // On vide la RAM utilisée par les images préchargées
            _imageCache?.Clear();

            // 5. Enfin, on vide la scène 3D
            viewport3D.Children.Clear();

            // 6. Libération du WebView2 (carte GPS)
            webViewMap?.Dispose();

            //🤔❓ A ajouter ??🤔❓
            //// 7. Annulation de toute requête de géocodage en cours
            //_ctsGeocodage?.Cancel();
            //_ctsGeocodage?.Dispose();
            //_ctsGeocodage = null;
            //// 8. Annulation de toute requête HTTP en cours
            //_httpClientGeocodage?.CancelPendingRequests();
            //_httpClientGeocodage?.Dispose();
            //// 9. Annulation de toute tâche asynchrone en cours
            //_ = Task.Run(() =>
            //{
            //    // On attend un peu pour que les tâches en cours se terminent
            //    Thread.Sleep(500);
            //    _ctsGeocodage?.Cancel();
            //    _ctsGeocodage?.Dispose();
            //    _httpClientGeocodage?.CancelPendingRequests();
            //    _httpClientGeocodage?.Dispose();
            //});

            Mouse.OverrideCursor = null;
        }
    }
}