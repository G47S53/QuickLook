var map = L.map('map', {
    zoomControl: true,
    attributionControl: true
}).setView([0, 0], 2);
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap'
}).addTo(map);

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
// Taille fixe en pixels écran (indépendante du niveau de zoom de la carte), redessiné
// à chaque zoom/déplacement pour garder cette taille visuelle constante.
var fovTriangle = null;
var fovEtat = null; // mémorise {lat, lon, directionDeg, fovDeg, rayonPixels}

function setFovTriangle(lat, lon, directionDeg, fovDeg, rayonPixels) {
    fovEtat = { lat: lat, lon: lon, directionDeg: directionDeg, fovDeg: fovDeg, rayonPixels: rayonPixels };
    redessinerFovTriangle();
}

function redessinerFovTriangle() {
    if (fovEtat === null) return;

    var pointeOrigine = map.latLngToContainerPoint([fovEtat.lat, fovEtat.lon]);

    var angleDebut = fovEtat.directionDeg - fovEtat.fovDeg / 2;
    var angleFin = fovEtat.directionDeg + fovEtat.fovDeg / 2;

    // Approximation de l'arc par des segments : plus nbSegments est grand, plus l'arc est lisse.
    // 24 segments suffit largement pour un secteur de taille modeste (rayonPixels ~100).
    var nbSegments = 24;
    var points = [[fovEtat.lat, fovEtat.lon]];

    for (var i = 0; i <= nbSegments; i++) {
        var angleDeg = angleDebut + (angleFin - angleDebut) * (i / nbSegments);
        var angleRad = angleDeg * Math.PI / 180;

        var pointPx = L.point(
            pointeOrigine.x + fovEtat.rayonPixels * Math.sin(angleRad),
            pointeOrigine.y - fovEtat.rayonPixels * Math.cos(angleRad)
        );

        points.push(map.containerPointToLatLng(pointPx));
    }

    if (fovTriangle === null) {
        fovTriangle = L.polygon(points, {
            color: '#FF3B3B',
            weight: 1,
            fillColor: '#FF3B3B',
            fillOpacity: 0.35,
            interactive: false,
            className: 'fov-triangle'
        }).addTo(map);
    } else {
        fovTriangle.setLatLngs(points);
    }
}

function hideFovTriangle() {
    if (fovTriangle !== null) {
        map.removeLayer(fovTriangle);
        fovTriangle = null;
    }
    fovEtat = null;
}

map.on('zoom move', redessinerFovTriangle);

// Pendant l'animation de zoom, la position pixel du triangle calculée par
// latLngToContainerPoint() ne suit pas exactement la transition visuelle de la carte,
// ce qui crée un effet de saut visible à la fin du zoom. On masque donc le triangle
// (simple opacité, pas de removeLayer — léger, aucun rechargement) pendant la transition,
// et on le réaffiche une fois la carte stabilisée, repositionné correctement.
map.on('zoomstart', function () {
    if (fovTriangle !== null) {
        fovTriangle.setStyle({ opacity: 0, fillOpacity: 0 });
    }
});

map.on('zoomend', function () {
    if (fovTriangle !== null) {
        redessinerFovTriangle();
        fovTriangle.setStyle({ opacity: 1, fillOpacity: 0.35 });
    }
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