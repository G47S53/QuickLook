// ── Définition des couches de base ──

// 1. Couche Plan (OpenStreetMap)
var osmLayer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap'
});

// 2. Couche Satellite (Esri World Imagery)
var satelliteLayer = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
    maxZoom: 19, // Esri supporte généralement jusqu'au zoom 18-19 selon les zones
    attribution: 'Tiles &copy; Esri &mdash; Source: Esri, i-cubed, USDA, USGS, AEX, GeoEye, Getmapping, Aerogrid, IGN, IGP, UPR-EGP, and the GIS User Community'
});

// ── Initialisation de la carte ──
// On charge 'osmLayer' par défaut dans le tableau 'layers'
var map = L.map('map', {
    zoomControl: true,
    attributionControl: true,
    rotate: true,           // ← active la rotation
    bearing: 0,              // ← cap initial (0° = nord en haut)
    rotateControl: {         // ← la boussole cliquable, en haut à gauche par défaut
        position: 'topleft',
        closeOnZeroBearing: false  // garde la boussole visible même à 0°
    },
    layers: [osmLayer]
}).setView([0, 0], 2);


// ── Contrôle des couches (Switch Plan/Satellite) ──
var baseMaps = {
    "Plan": osmLayer,
    "Satellite": satelliteLayer
};

// Ajoute le petit menu volant en haut à droite
L.control.layers(baseMaps, null, { position: 'topright' }).addTo(map);

// Ajoute l'echelle à la carte
L.control.scale().addTo(map);


// ── Barre de recherche (geocoding via OpenStreetMap/Nominatim, gratuit, sans clé API) ──
// style: 'button' affiche une icône loupe qui révèle la barre de recherche au clic.
// showMarker/showPopup à false : la recherche ne fait que centrer la carte, elle ne crée
// jamais de marqueur ni ne modifie le GPS de la photo (ça reste le rôle du clic droit).
var geoSearchProvider = new window.GeoSearch.OpenStreetMapProvider();
var geoSearchControl = new window.GeoSearch.GeoSearchControl({
    provider: geoSearchProvider,
    style: 'button',
    showMarker: false,
    showPopup: false,
    autoClose: true,
    searchLabel: 'Rechercher un lieu...'
});
map.addControl(geoSearchControl);

var marker = null;

function setPosition(lat, lon) {
    var latLng = [lat, lon];
    if (marker === null) {
        marker = L.marker(latLng).addTo(map);
    } else {
        marker.setLatLng(latLng);
    }
    map.setView(latLng, 15);
}

function recentrerSurMarqueur() {
    if (marker !== null) {
        map.setView(marker.getLatLng(), map.getZoom());
    }
}

// ── Triangle FOV (champ de vision du panorama) ──
// Remplacement de L.polygon par un L.marker contenant un cône en SVG (L.divIcon).
// Cela permet de conserver une taille fixe en pixels écran de manière structurelle.
// Lors des zooms ou déplacements, Leaflet déplace le marqueur sans déformer le SVG,
// éliminant l'effet de saut et évitant d'avoir à masquer le triangle !
var fovTriangle = null;
var fovEtat = null; 

function setFovTriangle(lat, lon, directionDeg, fovDeg, rayonPixels) {
    fovEtat = { lat: lat, lon: lon, directionDeg: directionDeg, fovDeg: fovDeg, rayonPixels: rayonPixels };
    redessinerFovTriangle();
}

function redessinerFovTriangle() {
    if (fovEtat === null) return;

    var latLng = [fovEtat.lat, fovEtat.lon];
    var r = fovEtat.rayonPixels;
    var fov = fovEtat.fovDeg;

    // 1. Calcul des coordonnées de l'arc de cercle SVG (centré vers le haut, à 0°)
    var angleDebut = -fov / 2;
    var angleFin = fov / 2;

    var x1 = r + r * Math.sin(angleDebut * Math.PI / 180);
    var y1 = r - r * Math.cos(angleDebut * Math.PI / 180);
    var x2 = r + r * Math.sin(angleFin * Math.PI / 180);
    var y2 = r - r * Math.cos(angleFin * Math.PI / 180);

    // Large-arc-flag (0 pour un angle < 180°, ce qui est toujours le cas d'un FOV de panorama)
    var largeArcFlag = fov > 180 ? 1 : 0;

    // 2. Tracé du cône (un "camembert" pointant initialement vers le haut)
    var pathD = `M ${r} ${r} L ${x1} ${y1} A ${r} ${r} 0 ${largeArcFlag} 1 ${x2} ${y2} Z`;

    // 3. Gestion de la rotation (Cap de la photo + orientation éventuelle de la carte)
    var bearing = map.getBearing ? map.getBearing() : 0;
    
    // Note : Si votre plugin de rotation fait déjà pivoter nativement les marqueurs avec la carte,
    // il vous suffira de retirer le bearing et de mettre : var rotationTotale = fovEtat.directionDeg;
    var rotationTotale = fovEtat.directionDeg + bearing;

    // Génération du contenu HTML avec le SVG inline et la rotation CSS
    var svgHtml = `
        <svg width="${r * 2}" height="${r * 2}" style="transform: rotate(${rotationTotale}deg); transform-origin: center; display: block;">
            <path d="${pathD}" fill="#FF3B3B" fill-opacity="0.35" stroke="#FF3B3B" stroke-width="1" />
        </svg>
    `;

    // 4. Création ou mise à jour du marqueur Leaflet
    if (fovTriangle === null) {
        var fovIcon = L.divIcon({
            html: svgHtml,
            className: 'fov-marker-container',
            iconSize: [r * 2, r * 2],
            iconAnchor: [r, r] // Aligne le centre du SVG (r, r) sur les coordonnées GPS
        });

        fovTriangle = L.marker(latLng, {
            icon: fovIcon,
            interactive: false // Permet de cliquer à travers le triangle sans bloquer la carte
        }).addTo(map);
    } else {
        fovTriangle.setLatLng(latLng);
        fovTriangle.setIcon(L.divIcon({
            html: svgHtml,
            className: 'fov-marker-container',
            iconSize: [r * 2, r * 2],
            iconAnchor: [r, r]
        }));
    }
}

function hideFovTriangle() {
    if (fovTriangle !== null) {
        map.removeLayer(fovTriangle);
        fovTriangle = null;
    }
    fovEtat = null;
}

// ── Gestion des événements ──
// Plus besoin d'écouter 'zoom' et 'move' pour recalculer le tracé, le L.marker suit la carte nativement !
// On écoute uniquement 'rotate' pour réorienter le cône SVG si la boussole ou la carte tourne.
map.on('rotate', redessinerFovTriangle);

// Nettoyage et préservation du correctif matériel de focus
map.on('zoomend', function () {
    // 🕹️ CORRECTIF SPACEMOUSE : Redonne le focus à QuickLook dès que le zoom se termine
    window.chrome.webview.postMessage({ type: 'restore_focus' });
});

// 🕹️ CORRECTIF SPACEMOUSE : Redonne le focus à QuickLook dès qu'on relâche un clic GAUCHE ou un drag
map.on('mouseup', function (e) {
    // Sécurité : On ne déclenche QUE sur le clic GAUCHE (button === 0).
    // On ignore le clic droit (2) pour laisser le menu contextuel s'ouvrir tranquillement.
    if (e.originalEvent && e.originalEvent.button !== 0) return;

    setTimeout(function () {
        var activeEl = document.activeElement;
        // Sécurité : Si l'utilisateur clique dans le champ de recherche, on ne vole pas le focus !
        if (activeEl && activeEl.tagName !== 'INPUT' && activeEl.tagName !== 'TEXTAREA') {
            window.chrome.webview.postMessage({ type: 'restore_focus' });
        }
    }, 50);
});

// ── Correctif taille ──
// La WebView2 est hébergée dans un panneau WPF redimensionnable : invalidateSize() force
// Leaflet à recalculer ses dimensions ; le ResizeObserver le déclenche automatiquement à
// chaque changement réel de taille du div #map.
var mapDiv = document.getElementById('map');
var resizeObserver = new ResizeObserver(function () {
    map.invalidateSize();
});
resizeObserver.observe(mapDiv);

window.addEventListener('load', function () {
    setTimeout(function () { map.invalidateSize(); }, 200);
});

function invalidateMapSize() {
    map.invalidateSize();
}

// ── Clic droit personnalisé ──
// Le menu natif de Chromium est désactivé côté C# (AreDefaultContextMenusEnabled = false).
map.on('contextmenu', function (e) {
    window.chrome.webview.postMessage({
        type: 'contextmenu',
        lat: e.latlng.lat,
        lon: e.latlng.lng,
        containerX: e.containerPoint.x,
        containerY: e.containerPoint.y
    });
});