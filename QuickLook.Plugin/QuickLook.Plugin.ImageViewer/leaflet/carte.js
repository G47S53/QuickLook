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

    var rad1 = (fovEtat.directionDeg - fovEtat.fovDeg / 2) * Math.PI / 180;
    var rad2 = (fovEtat.directionDeg + fovEtat.fovDeg / 2) * Math.PI / 180;

    // En pixels écran, 0° (Nord) doit pointer vers le haut (Y décroissant), sens horaire.
    var sommet1Px = L.point(
        pointeOrigine.x + fovEtat.rayonPixels * Math.sin(rad1),
        pointeOrigine.y - fovEtat.rayonPixels * Math.cos(rad1)
    );
    var sommet2Px = L.point(
        pointeOrigine.x + fovEtat.rayonPixels * Math.sin(rad2),
        pointeOrigine.y - fovEtat.rayonPixels * Math.cos(rad2)
    );

    var points = [
        [fovEtat.lat, fovEtat.lon],
        map.containerPointToLatLng(sommet1Px),
        map.containerPointToLatLng(sommet2Px)
    ];

    if (fovTriangle === null) {
        fovTriangle = L.polygon(points, {
            color: '#FF3B3B',
            weight: 1,
            fillColor: '#FF3B3B',
            fillOpacity: 0.35,
            interactive: false
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