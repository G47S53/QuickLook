using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.ImageViewer.Pano360
{
    // Déclaration de la classe à utiliser ultérieurement dans Pano360Panel.xaml.cs
    public class PanoMetadata
    {
        // Données XMP
        public short? Rating { get; set; }
        public string Label { get; set; }
        public string Titre { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
        // Données EXIF
        public ushort? Iso { get; set; }
        public string ShutterSpeed { get; set; }
        public string DateTimeOriginal { get; set; }
        public string Date { get; set; }
        public string Time { get; set; }
        public string Model { get; set; }
        // Données GPS (décimal signé, ex: "49.598494" / "3.015321")
        public string GpsLatitude { get; set; }
        public string GpsLongitude { get; set; }
        public string GpsAltitude { get; set; }
        // Perso (Pano360Panel) : localisation textuelle issue du géocodage inverse (Nominatim/OSM), mise en cache
        // dans les métadonnées pour éviter de refaire un appel réseau à chaque ouverture de la photo.
        // City/State/Country/CountryCode/Sublocation sont stockés dans les schémas XMP standard IPTC Core /
        // photoshop (compatibles Lightroom, Bridge, ACDSee...) ; seuls PostCode et County restent dans le
        // namespace personnalisé à ce plugin, faute d'équivalent simple côté standard (cf. PanoMetadataService).
        public string LocHouseNumber { get; set; }               // Numéro de rue (ex: "12"), disponible seulement à zoom élevé (18).
                                                                 // Fusionné avec LocRoad à l'écriture (Iptc4xmpCore:Location/Sublocation, qui n'a
                                                                 // qu'un seul champ texte libre) ; reste vide après une relecture depuis le fichier,
                                                                 // le numéro étant alors déjà inclus dans LocRoad.
        public string LocRoad { get; set; }                      // Nom de rue (ex: "Rue de la Paix"), ou "numéro + rue" après relecture (cf. ci-dessus).
        public string LocCity { get; set; }                      // Ville/village/commune (fallback Town/Village/Municipality déjà géré côté Nominatim).
        public string LocPostCode { get; set; }                  // Code postal.
        public string LocCounty { get; set; }                    // Comté/département (équivalent administratif intermédiaire).
        public string LocState { get; set; }                     // Région/état.
        public string LocCountry { get; set; }                   // Pays (nom complet, dans la langue demandée à Nominatim).
        public string LocCountryCode { get; set; }               // Code pays ISO (ex: "fr").

        // Au moins une des informations de localisation ci-dessus est-elle déjà renseignée ?
        // Sert à décider si on peut éviter un appel réseau Nominatim (cf. MettreAJourLocalisationTexteAsync).
        public bool ALocalisationConnue =>
            !string.IsNullOrEmpty(LocCity) || !string.IsNullOrEmpty(LocCountry) ||
            !string.IsNullOrEmpty(LocRoad) || !string.IsNullOrEmpty(LocCounty) ||
            !string.IsNullOrEmpty(LocState) || !string.IsNullOrEmpty(LocPostCode);

        // Perso (Pano360Panel) : orientation du panorama (XMP GPano, standard Google Photo Sphere), mémorisée par photo pour retrouver le même cadrage au prochain visionnage.
        public double? PoseHeadingDegrees { get; set; }         // Orientation du panorama (XMP GPano, standard Google Photo Sphere) : cap compass
                                                                // en degrés, 0=Nord, sens horaire, pour le centre de l'image. null si absent du fichier.

        // Perso (Pano360Panel) : niveau de zoom Leaflet de la carte GPS (panneau pnlMapContainer), mémorisé par photo pour retrouver le même cadrage au prochain visionnage.
        public int? MapZoomLevel { get; set; }                  // Niveau de zoom Leaflet de la carte GPS (panneau pnlMapContainer), mémorisé par photo pour
                                                                // retrouver le même cadrage au prochain visionnage (ex: zoom serré pour l'intérieur d'une
                                                                // cathédrale, zoom large pour Monument Valley). Namespace XMP personnalisé, propre à ce plugin.
        public double? HomeHorizontalSphere { get; set; }
        public double? HomeVerticalSphere { get; set; }
        public double? HomeFovSphere { get; set; }
    }

    public static class PanoMetadataService
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constantes pour la lecture/écriture des métadonnées
        // ─────────────────────────────────────────────────────────────────────
        private const string DjiNamespace = "/xmp/http\\/:\\/\\/www.dji.com\\/drone-dji\\/1.0\\/";                  // Le namespace DJI dans le XMP utilise cette URI comme clé de requête WPF.
                                                                                                                    // Partagée entre lecture (TentativeLectureGpsXmpDji) et écriture (mise à jour de cohérence).
        private const string Pano360PanelNamespace = "/xmp/http\\:\\/\\/www.pano360panel.local\\/ns\\/1.0\\/";      // Namespace XMP personnalisé pour les données propres à ce plugin (pas un standard externe).
                                                                                                                    // URI arbitraire mais stable : sert uniquement de clé d'identification unique, n'a pas besoin
                                                                                                                    // de pointer vers une page web réelle.
        private const string GpanoNamespace = "/xmp/http\\:\\/\\/ns.google.com\\/photos\\/1.0\\/panorama\\/";       // Namespace XMP standard Google Photo Sphere (GPano) pour l'orientation du panorama.

        // Namespaces standard pour la localisation (IPTC Core + photoshop), lus/écrits nativement par
        // Lightroom, Bridge, Photoshop, ACDSee, etc. Pas de préfixe à déclarer explicitement : WPF résout
        // tout seul le bon namespace XML à partir du nom court "Iptc4xmpCore:"/"photoshop:" (cf.
        // BitmapMetadata.SetQuery, qui connaît ces préfixes courants nativement).
        private const string PhotoshopNamespace = "/xmp/photoshop:";                                                // Répartition des champs entre les deux schémas (historique du standard, pas un choix arbitraire) :
                                                                                                                    // - photoshop:City / photoshop:State / photoshop:Country / photoshop:CountryCode
                                                                                                                    // → c'est CE namespace, et non Iptc4xmpCore, qui porte historiquement City/State/Country dans
                                                                                                                    // le panneau "IPTC Core" d'Adobe (Lightroom/Bridge écrivent réellement ici en pratique).
        private const string Iptc4xmpCoreNamespace = "/xmp/Iptc4xmpCore:";                                          // - Iptc4xmpCore:Location (= "Sublocation" depuis IPTC Core 1.1) et Iptc4xmpCore:CountryCode
                                                                                                                    // → utilisés en complément/miroir pour la compatibilité avec les logiciels qui lisent le
                                                                                                                    // schéma Iptc4xmpCore plutôt que photoshop.
        // ─────────────────────────────────────────────────────────────────────
        // LECTURE DES MÉTADONNÉES
        // ─────────────────────────────────────────────────────────────────────
        public static PanoMetadata ReadMetadata(string filePath)
        {
            var data = new PanoMetadata();

            try
            {
                if (!File.Exists(filePath)) return data;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

                    if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata bitmapMetadata)
                    {
                        // ──── 1. Lecture des données XMP ────────────────────────────────────

                        // Étoiles (Rating)
                        if (bitmapMetadata.ContainsQuery("/xmp/xmp:Rating"))
                        {
                            var ratingRaw = bitmapMetadata.GetQuery("/xmp/xmp:Rating");
                            if (ratingRaw != null && short.TryParse(ratingRaw.ToString(), out short r))
                                data.Rating = r;
                        }

                        // Couleur (Label)
                        if (bitmapMetadata.ContainsQuery("/xmp/xmp:Label"))
                        {
                            data.Label = bitmapMetadata.GetQuery("/xmp/xmp:Label") as string;
                        }

                        // Titre
                        if (bitmapMetadata.ContainsQuery("/xmp/dc:title/x-default"))
                        {
                            data.Titre = bitmapMetadata.GetQuery("/xmp/dc:title/x-default") as string;
                        }

                        // Mots clé (bag XMP dc:subject : nombre d'éléments variable)
                        int indexMotCle = 0;
                        while (bitmapMetadata.ContainsQuery($"/xmp/dc:subject/{{ulong={indexMotCle}}}"))
                        {
                            var motCle = bitmapMetadata.GetQuery($"/xmp/dc:subject/{{ulong={indexMotCle}}}") as string;
                            if (!string.IsNullOrEmpty(motCle))
                                data.Keywords.Add(motCle);

                            indexMotCle++;
                        }

                        // ──── 2. Lecture des données EXIF ────────────────────────────────────

                        // ISO
                        if (bitmapMetadata.ContainsQuery("/app1/ifd/exif/{ushort=34855}"))
                        {
                            data.Iso = bitmapMetadata.GetQuery("/app1/ifd/exif/{ushort=34855}") as ushort?;
                        }

                        // Vitesse d'obturation (ExposureTime)
                        if (bitmapMetadata.ContainsQuery("/app1/ifd/exif/{ushort=33434}"))
                        {
                            var shutterSpeedRaw = bitmapMetadata.GetQuery("/app1/ifd/exif/{ushort=33434}");
                            data.ShutterSpeed = FormatExposureTime(shutterSpeedRaw);
                        }

                        // DateTimeOriginal
                        if (bitmapMetadata.ContainsQuery("/app1/{ushort=0}/{ushort=34665}/{ushort=36867}"))
                        {
                            data.DateTimeOriginal = bitmapMetadata.GetQuery("/app1/{ushort=0}/{ushort=34665}/{ushort=36867}")?.ToString();
                            data.Date = DateFormatage(data.DateTimeOriginal);
                            data.Time = HeureFormatage(data.DateTimeOriginal);
                        }

                        // Model (type de caméra)
                        if (bitmapMetadata.ContainsQuery("/app1/{ushort=0}/{ushort=272}"))
                        {
                            data.Model = bitmapMetadata.GetQuery("/app1/{ushort=0}/{ushort=272}")?.ToString();
                            if (data.Model == "OQ001") data.Model = "Osmo 360";
                        }

                        // ──── 3. Lecture des données GPS ─────────────────────────────────────
                        //
                        // Stratégie : EXIF GPS en source principale (standard universel),
                        //             XMP DJI en fallback (spécifique DJI Osmo/Mini/Air).
                        //
                        // Si les deux sont présents, EXIF est prioritaire car c'est le
                        // standard géographique de référence (WGS-84, même donnée, plus fiable).

                        bool gpsLuDepuisExif = TentativeLectureGpsExif(bitmapMetadata, data);

                        if (!gpsLuDepuisExif)
                        {
                            TentativeLectureGpsXmpDji(bitmapMetadata, data);
                        }
                        // Rempli les champs avec des 0 si il n'y a rien et pour éviter les bugs avec la carte
                        data.GpsLatitude = data.GpsLatitude ?? "0";
                        data.GpsLongitude = data.GpsLongitude ?? "0";
                        data.GpsAltitude = data.GpsAltitude ?? "0";
                        System.Diagnostics.Debug.WriteLine($"[Metadata] GPS final : {data.GpsLatitude}, {data.GpsLongitude}, alt={data.GpsAltitude}m");

                        // ──── 4. Lecture de l'orientation du panorama (XMP GPano, standard Google Photo Sphere) ────
                        TentativeLecturePoseHeadingDegrees(bitmapMetadata, data);

                        // ──── 5. Lecture du niveau de zoom de la carte (XMP namespace personnalisé) ────
                        TentativeLectureMapZoomLevel(bitmapMetadata, data);

                        // ──── 6. Lecture de la localisation textuelle mise en cache (XMP namespace personnalisé) ────
                        TentativeLectureLocalisation(bitmapMetadata, data);

                        // ──── 7. Lecture de la position home du panorama mise en cache (XMP namespace personnalisé) ────
                        TentativeLectureSphereHome(bitmapMetadata, data);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Erreur de lecture sur {Path.GetFileName(filePath)} : {ex.Message}");
            }

            return data;
        }
        // ─────────────────────────────────────────────────────────────────────
        // ÉCRITURE DES MÉTADONNÉES
        // ─────────────────────────────────────────────────────────────────────
        public static void WriteMetadata(string filePath, PanoMetadata data)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                string tempPath = filePath + ".tmp";

                using (var fromStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(fromStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        var metadataClone = frame.Metadata is BitmapMetadata bm ? bm.Clone() : new BitmapMetadata("jpg");

                        // ── 1. Rating ────────────────────────────────────────────────────────────────────────────────────
                        if (data.Rating.HasValue)
                            metadataClone.SetQuery("/xmp/xmp:Rating", data.Rating.Value.ToString());
                        else if (metadataClone.ContainsQuery("/xmp/xmp:Rating"))
                            metadataClone.RemoveQuery("/xmp/xmp:Rating");

                        // ── 2. Label ─────────────────────────────────────────────────────────────────────────────────────
                        if (data.Label != null)
                            metadataClone.SetQuery("/xmp/xmp:Label", data.Label);

                        // ── 3. GPS ───────────────────────────────────────────────────────────────────────────────────────
                        // (EXIF standard uniquement — jamais XMP DJI, qui reste un format de lecture/fallback)
                        if (data.GpsLatitude != null && data.GpsLongitude != null &&
                            double.TryParse(data.GpsLatitude, NumberStyles.Float, CultureInfo.InvariantCulture, out double latDecimal) &&
                            double.TryParse(data.GpsLongitude, NumberStyles.Float, CultureInfo.InvariantCulture, out double lonDecimal))
                        {
                            try
                            {
                                // WPF a besoin que le sous-bloc IFD GPS existe avant qu'on puisse y écrire des tags enfants.
                                // S'il n'existe pas encore (cas typique d'un JPEG DJI qui n'a que du XMP, jamais d'EXIF GPS),
                                // on le crée explicitement avec un BitmapMetadata vide avant d'y poser les valeurs.
                                if (!metadataClone.ContainsQuery("/app1/ifd/gps"))
                                {
                                    metadataClone.SetQuery("/app1/ifd/gps", new BitmapMetadata("gps"));
                                }

                                var (latDms, latRef) = ConvertirGpsDecimalVersDms(latDecimal, estLatitude: true);
                                var (lonDms, lonRef) = ConvertirGpsDecimalVersDms(lonDecimal, estLatitude: false);

                                metadataClone.SetQuery("/app1/ifd/gps/{ushort=1}", latRef);
                                metadataClone.SetQuery("/app1/ifd/gps/{ushort=2}", latDms);
                                metadataClone.SetQuery("/app1/ifd/gps/{ushort=3}", lonRef);
                                metadataClone.SetQuery("/app1/ifd/gps/{ushort=4}", lonDms);

                                // Altitude (optionnelle)
                                if (data.GpsAltitude != null &&
                                    double.TryParse(data.GpsAltitude, NumberStyles.Float, CultureInfo.InvariantCulture, out double altDecimal))
                                {
                                    byte altRef = altDecimal < 0 ? (byte)1 : (byte)0;
                                    ulong altRationnel = EncodeRationnel(Math.Abs(altDecimal), 10); // 1 décimale de précision

                                    metadataClone.SetQuery("/app1/ifd/gps/{ushort=5}", altRef);
                                    metadataClone.SetQuery("/app1/ifd/gps/{ushort=6}", altRationnel);
                                }

                                System.Diagnostics.Debug.WriteLine($"[Metadata] GPS écrit dans EXIF : {data.GpsLatitude}, {data.GpsLongitude}, alt={data.GpsAltitude}m");

                                // Cohérence avec les logiciels qui privilégient (ou ne lisent que) le XMP DJI en lecture
                                // (ex: ACDSee) : si ces champs existent déjà sur ce fichier, on les met à jour aussi,
                                // pour éviter qu'un logiciel tiers affiche encore l'ancienne position GPS périmée.
                                if (metadataClone.ContainsQuery(DjiNamespace + ":GpsLatitude"))
                                {
                                    metadataClone.SetQuery(DjiNamespace + ":GpsLatitude", latDecimal.ToString(CultureInfo.InvariantCulture));
                                    metadataClone.SetQuery(DjiNamespace + ":GpsLongitude", lonDecimal.ToString(CultureInfo.InvariantCulture));

                                    if (data.GpsAltitude != null && metadataClone.ContainsQuery(DjiNamespace + ":AbsoluteAltitude") &&
                                        double.TryParse(data.GpsAltitude, NumberStyles.Float, CultureInfo.InvariantCulture, out double altPourDji))
                                    {
                                        metadataClone.SetQuery(DjiNamespace + ":AbsoluteAltitude", altPourDji.ToString(CultureInfo.InvariantCulture));
                                    }

                                    System.Diagnostics.Debug.WriteLine("[Metadata] GPS également mis à jour dans XMP DJI (cohérence multi-logiciels)");
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec écriture GPS EXIF : {ex.Message}");
                            }
                        }

                        // ── 4. Champs perso ──────────────────────────────────────────────────────────────────────────────
                        // ── Orientation du panorama ────────────────────────────────────────────────────────────────────── 
                        // Orientation du panorama (XMP GPano:PoseHeadingDegrees, standard Google Photo Sphere).
                        // C'est ce champ que les logiciels de visite virtuelle comme Pano2Vr lisent pour orienter
                        // le panorama sur leur propre outil carte.
                        if (data.PoseHeadingDegrees.HasValue)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Metadata] PoseHeadingDegrees est : {data.PoseHeadingDegrees.Value.ToString(CultureInfo.InvariantCulture)}");
                            try
                            {
                                // Comme pour le bloc GPS EXIF, WPF a besoin que le bloc XMP racine existe avant
                                // qu'on puisse y écrire un namespace personnalisé (GPano). S'il n'existe pas encore
                                // (fichier qui n'a jamais eu de XMP du tout), on le crée explicitement.
                                if (!metadataClone.ContainsQuery("/xmp"))
                                {
                                    metadataClone.SetQuery("/xmp", new BitmapMetadata("xmp"));
                                }

                                metadataClone.SetQuery(GpanoNamespace + ":PoseHeadingDegrees",
                                    data.PoseHeadingDegrees.Value.ToString(CultureInfo.InvariantCulture));

                                System.Diagnostics.Debug.WriteLine($"[Metadata] PoseHeadingDegrees écrit : {data.PoseHeadingDegrees.Value}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec écriture PoseHeadingDegrees : {ex.Message}");
                            }
                        }

                        // ── Niveau de zoom de la carte GPS (namespace XMP personnalisé à ce plugin). ─────────────────────
                        if (data.MapZoomLevel.HasValue)
                        {
                            try
                            {
                                if (!metadataClone.ContainsQuery("/xmp"))
                                {
                                    metadataClone.SetQuery("/xmp", new BitmapMetadata("xmp"));
                                }

                                metadataClone.SetQuery(Pano360PanelNamespace + ":MapZoomLevel",
                                    data.MapZoomLevel.Value.ToString(CultureInfo.InvariantCulture));

                                System.Diagnostics.Debug.WriteLine($"[Metadata] MapZoomLevel écrit : {data.MapZoomLevel.Value}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec écriture MapZoomLevel : {ex.Message}");
                            }
                        }

                        // ── Position Home de la sphère (horizontal, vertical et FOV) ─────────────────────────────────────
                        if (data.HomeHorizontalSphere.HasValue && data.HomeVerticalSphere.HasValue && data.HomeFovSphere.HasValue) 
                        {
                            try
                            {
                                if (!metadataClone.ContainsQuery("/xmp"))
                                {
                                    metadataClone.SetQuery("/xmp", new BitmapMetadata("xmp"));
                                }

                                metadataClone.SetQuery(Pano360PanelNamespace + ":HomeHorizontalSphere", data.HomeHorizontalSphere.Value.ToString(CultureInfo.InvariantCulture));
                                metadataClone.SetQuery(Pano360PanelNamespace + ":HomeVerticalSphere", data.HomeVerticalSphere.Value.ToString(CultureInfo.InvariantCulture));
                                metadataClone.SetQuery(Pano360PanelNamespace + ":HomeFovSphere", data.HomeFovSphere.Value.ToString(CultureInfo.InvariantCulture));

                                System.Diagnostics.Debug.WriteLine($"[Metadata] SphereHome écrit : {data.HomeHorizontalSphere} - {data.HomeVerticalSphere} - {data.HomeFovSphere}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec écriture SphereHome : {ex.Message}");
                            }
                        }

                        // Localisation textuelle (géocodage inverse Nominatim mis en cache), écrite dans les
                        // schémas XMP standard IPTC Core / photoshop pour une compatibilité maximale avec
                        // Lightroom, Bridge, Photoshop, ACDSee, etc. — pas seulement avec ce plugin.
                        // City/State/Country/CountryCode partent dans photoshop:* (lu en priorité par Adobe),
                        // avec un miroir Iptc4xmpCore:CountryCode et Iptc4xmpCore:Location (Sublocation, qui
                        // reçoit l'adresse "numéro + rue" faute de champ standard plus spécifique).
                        // PostCode et County restent dans le namespace personnalisé : pas d'équivalent simple
                        // dans IPTC Core (le comté/département n'existe que côté IPTC Extension, structure en
                        // Bag imbriqué non gérée par BitmapMetadata.SetQuery).
                        // On n'écrit que les champs renseignés : un champ resté à null (jamais récupéré par
                        // Nominatim pour cette position, ex : pas de numéro de rue en zone rurale) n'écrase pas
                        // une éventuelle valeur déjà présente dans le fichier.
                        if (data.ALocalisationConnue)
                        {
                            try
                            {
                                if (!metadataClone.ContainsQuery("/xmp"))
                                {
                                    metadataClone.SetQuery("/xmp", new BitmapMetadata("xmp"));
                                }

                                // Standard photoshop (lu par Lightroom/Bridge/ACDSee dans leur panneau IPTC Core)
                                EcrireChampTexte(metadataClone, PhotoshopNamespace + "City", data.LocCity);
                                EcrireChampTexte(metadataClone, PhotoshopNamespace + "State", data.LocState);
                                EcrireChampTexte(metadataClone, PhotoshopNamespace + "Country", data.LocCountry);
                                EcrireChampTexte(metadataClone, PhotoshopNamespace + "CountryCode", data.LocCountryCode);

                                // Miroir standard IPTC Core (CountryCode officiel du schéma + Sublocation pour l'adresse)
                                EcrireChampTexte(metadataClone, Iptc4xmpCoreNamespace + "CountryCode", data.LocCountryCode);
                                string sublocation = ((data.LocHouseNumber ?? "") + " " + (data.LocRoad ?? "")).Trim();
                                EcrireChampTexte(metadataClone, Iptc4xmpCoreNamespace + "Location", sublocation);

                                // Namespace personnalisé : champs sans équivalent standard simple (Code postal, Comté)
                                EcrireChampTexte(metadataClone, Pano360PanelNamespace + ":LocPostCode", data.LocPostCode);
                                EcrireChampTexte(metadataClone, Pano360PanelNamespace + ":LocCounty", data.LocCounty);

                                System.Diagnostics.Debug.WriteLine($"[Metadata] Localisation écrite (IPTC/photoshop) : {data.LocCity} / {data.LocCountry}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec écriture localisation : {ex.Message}");
                            }
                        }

                        // ── 5. Titre ─────────────────────────────────────────────────────────────────────────────────────
                        // Titre (XMP dc:title, structure LangAlt — on écrit dans la variante "x-default",
                        // celle lue par défaut par WPF et par la quasi-totalité des logiciels). Une chaîne
                        // vide/null efface le titre existant plutôt que de laisser une ancienne valeur périmée.
                        if (!metadataClone.ContainsQuery("/xmp"))
                        {
                            metadataClone.SetQuery("/xmp", new BitmapMetadata("xmp"));
                        }

                        if (!string.IsNullOrEmpty(data.Titre))
                        {
                            metadataClone.SetQuery("/xmp/dc:title/x-default", data.Titre);
                            System.Diagnostics.Debug.WriteLine($"[Metadata] Titre écrit : {data.Titre}");
                        }
                        else if (metadataClone.ContainsQuery("/xmp/dc:title/x-default"))
                        {
                            metadataClone.RemoveQuery("/xmp/dc:title/x-default");
                            System.Diagnostics.Debug.WriteLine("[Metadata] Titre effacé (vide)");
                        }

                        // ── 6. Mots clé ───────────────────────────────────────────────────────────────────────────────────
                        // (bag XMP dc:subject : nombre d'éléments variable)
                        try
                        {
                            // On vide d'abord les entrées existantes, sinon SetQuery se contente
                            // d'écraser les index déjà présents et laisse les anciens en trop si
                            // la nouvelle liste est plus courte que l'ancienne.
                            int indexExistant = 0;
                            while (metadataClone.ContainsQuery($"/xmp/dc:subject/{{ulong={indexExistant}}}"))
                            {
                                metadataClone.RemoveQuery($"/xmp/dc:subject/{{ulong={indexExistant}}}");
                                indexExistant++;
                            }

                            if (data.Keywords != null)
                            {
                                for (int i = 0; i < data.Keywords.Count; i++)
                                {
                                    metadataClone.SetQuery($"/xmp/dc:subject/{{ulong={i}}}", data.Keywords[i]);
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"[Metadata] Mots-clés écrits ({data.Keywords?.Count ?? 0}) : {string.Join(", ", data.Keywords ?? new List<string>())}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Metadata] Échec écriture mots-clés : {ex.Message}");
                        }

                        // ──────────────────────────────────────────────────────────────────────────────────────────────────
                        // ── 999. Reconstruction du fichier ────────────────────────────────────────────────────────────────
                        // ──────────────────────────────────────────────────────────────────────────────────────────────────
                        var encoder = new JpegBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(frame, frame.Thumbnail, metadataClone, frame.ColorContexts));

                        using (var toStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                        {
                            encoder.Save(toStream);
                        }
                    }
                }

                // Remplacement sécurisé
                File.Delete(filePath);
                File.Move(tempPath, filePath);
                System.Diagnostics.Debug.WriteLine($"[Metadata] Sauvegarde réussie pour {Path.GetFileName(filePath)} !");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Erreur d'écriture sur {Path.GetFileName(filePath)} : {ex.Message}");
            }
        }
        #region GPS
        // ─────────────────────────────────────────────────────────────────────
        //                            GPS
        // ═════════════════════════════════════════════════════════════════════
        // LECTURE GPS — Source 1 : EXIF standard (tous appareils photo/drone)
        // ─────────────────────────────────────────────────────────────────────
        // Retourne true si lat+lon ont bien été lus, false sinon.
        private static bool TentativeLectureGpsExif(BitmapMetadata bitmapMetadata, PanoMetadata data)
        {
            try
            {
                var gpsBlock = bitmapMetadata.GetQuery("/app1/ifd/gps") as BitmapMetadata;
                if (gpsBlock == null) return false;

                string latRef = gpsBlock.GetQuery("/{ushort=1}") as string; // "N" ou "S"
                string lonRef = gpsBlock.GetQuery("/{ushort=3}") as string; // "E" ou "W"
                var latRaw = gpsBlock.GetQuery("/{ushort=2}");            // UInt64[] DMS
                var lonRaw = gpsBlock.GetQuery("/{ushort=4}");            // UInt64[] DMS

                if (latRef == null || lonRef == null || latRaw == null || lonRaw == null)
                    return false;

                string lat = ConvertirGpsDms(latRaw, latRef);
                string lon = ConvertirGpsDms(lonRaw, lonRef);

                if (lat == null || lon == null) return false;

                data.GpsLatitude = lat;
                data.GpsLongitude = lon;

                // Altitude (tag 6 = rationnel UInt64, tag 5 = ref : 0=au-dessus, 1=en-dessous)
                var altRaw = gpsBlock.GetQuery("/{ushort=6}");
                if (altRaw is ulong altUlong)
                {
                    double alt = DecodeRationnel(altUlong);
                    var altRef = gpsBlock.GetQuery("/{ushort=5}");
                    if (altRef is byte altRefByte && altRefByte == 1)
                        alt = -alt;
                    data.GpsAltitude = alt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                }

                System.Diagnostics.Debug.WriteLine($"[Metadata] GPS lu depuis EXIF : {data.GpsLatitude}, {data.GpsLongitude}, alt={data.GpsAltitude}m");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec lecture GPS EXIF : {ex.Message}");
                return false;
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // LECTURE GPS — Source 2 : XMP DJI (fallback DJI Osmo/Mini/Air/Mavic)
        // ─────────────────────────────────────────────────────────────────────
        // Les données DJI sont déjà en degrés décimaux signés (+49.598494400),
        // ce qui évite toute conversion DMS. L'altitude "AbsoluteAltitude"
        // correspond à l'altitude GPS absolue (même donnée que EXIF tag 6).
        private static void TentativeLectureGpsXmpDji(BitmapMetadata bitmapMetadata, PanoMetadata data)
        {
            try
            {
                var latStr = bitmapMetadata.GetQuery(DjiNamespace + ":GpsLatitude") as string;
                var lonStr = bitmapMetadata.GetQuery(DjiNamespace + ":GpsLongitude") as string;
                var altStr = bitmapMetadata.GetQuery(DjiNamespace + ":AbsoluteAltitude") as string;

                // Latitude
                if (latStr != null && double.TryParse(latStr,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lat))
                {
                    data.GpsLatitude = lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                }

                // Longitude
                if (lonStr != null && double.TryParse(lonStr,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lon))
                {
                    data.GpsLongitude = lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                }

                // Altitude absolue
                if (altStr != null && double.TryParse(altStr,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double alt))
                {
                    data.GpsAltitude = alt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                }

                if (data.GpsLatitude != null)
                    System.Diagnostics.Debug.WriteLine($"[Metadata] GPS lu depuis XMP DJI : {data.GpsLatitude}, {data.GpsLongitude}, alt={data.GpsAltitude}m");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Échec lecture GPS XMP DJI : {ex.Message}");
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // HELPERS GPS
        // ─────────────────────────────────────────────────────────────────────
        private static string ConvertirGpsDms(object rawValue, string reference)
        {
            // Convertit un tableau DMS EXIF (3 rationnels UInt64) en degrés décimaux signés.
            // Chaque UInt64 encode : numérateur dans les 32 bits hauts, dénominateur dans les 32 bits bas.
            if (rawValue is ulong[] dms && dms.Length == 3)
            {
                double deg = DecodeRationnel(dms[0]);
                double min = DecodeRationnel(dms[1]);
                double sec = DecodeRationnel(dms[2]);

                double decimalDeg = deg + (min / 60.0) + (sec / 3600.0);

                if (reference == "S" || reference == "W")
                    decimalDeg = -decimalDeg;

                return decimalDeg.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            }
            return null;
        }
        private static double DecodeRationnel(ulong rationnel)
        {
            // Décode un rationnel EXIF encodé en UInt64 (numérateur << 32 | dénominateur).
            uint numerateur = (uint)(rationnel & 0xFFFFFFFF);  // bits bas  — ordre WPF (inverse spec EXIF)
            uint denominateur = (uint)(rationnel >> 32);                  // bits hauts
            return denominateur == 0 ? 0.0 : (double)numerateur / denominateur;
        }
        private static ulong EncodeRationnel(double valeur, uint denominateur)
        // Inverse de DecodeRationnel : encode une valeur décimale en rationnel EXIF/WPF
        // (numérateur dans les bits bas, dénominateur dans les bits hauts — ordre WPF, voir DecodeRationnel).
        {
            uint numerateur = (uint)Math.Round(valeur * denominateur);
            return ((ulong)denominateur << 32) | numerateur;
        }
        private static (ulong[] dms, string reference) ConvertirGpsDecimalVersDms(double decimalDeg, bool estLatitude)
        // Inverse de ConvertirGpsDms : convertit un degré décimal signé en triplet DMS
        // (degrés, minutes, secondes), chacun encodé en rationnel ulong, plus la référence N/S ou E/W.
        {
            string reference = estLatitude
                ? (decimalDeg >= 0 ? "N" : "S")
                : (decimalDeg >= 0 ? "E" : "W");

            double valeurAbsolue = Math.Abs(decimalDeg);

            int deg = (int)valeurAbsolue;
            double minutesRestantes = (valeurAbsolue - deg) * 60.0;
            int min = (int)minutesRestantes;
            double sec = (minutesRestantes - min) * 60.0;

            // Dénominateur 1 pour degrés/minutes (valeurs entières), 1000 pour les secondes
            // (3 décimales de précision sur les secondes, largement suffisant pour du GPS photo).
            ulong[] dms =
            {
        EncodeRationnel(deg, 1),
        EncodeRationnel(min, 1),
        EncodeRationnel(sec, 1000)
    };

            return (dms, reference);
        }
        #endregion GPS
        #region Perso
        // ─────────────────────────────────────────────────────────────────────
        //                            Perso
        // ═════════════════════════════════════════════════════════════════════
        // LECTURE ORIENTATION — XMP GPano:PoseHeadingDegrees (standard Google Photo Sphere)
        // ─────────────────────────────────────────────────────────────────────
        // Cap compass du centre de l'image, en degrés, 0=Nord, sens horaire. C'est le standard
        // utilisé par les logiciels de panorama (Hugin, PTGui, Pano2Vr...) pour orienter une
        // sphère sur une carte. Différent du GPS DJI : c'est un champ XMP générique, pas propriétaire.
        private static void TentativeLecturePoseHeadingDegrees(BitmapMetadata bitmapMetadata, PanoMetadata data)
        {
            try
            {
                var raw = bitmapMetadata.GetQuery(GpanoNamespace + ":PoseHeadingDegrees");

                if (raw != null && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double heading))
                {
                    data.PoseHeadingDegrees = heading;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Erreur de lecture PoseHeadingDegrees : {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine($"[Metadata] PoseHeadingDegrees : {data.PoseHeadingDegrees}");
        }
        // ─────────────────────────────────────────────────────────────────────
        // LECTURE ZOOM CARTE — XMP namespace personnalisé (pano360panel:MapZoomLevel)
        // ─────────────────────────────────────────────────────────────────────
        // Champ propre à ce plugin (pas un standard externe comme GPano) : niveau de zoom Leaflet
        // (entier, typiquement 1 à 19) appliqué à pnlMapContainer pour cette photo.
        private static void TentativeLectureMapZoomLevel(BitmapMetadata bitmapMetadata, PanoMetadata data)
        {
            try
            {
                var raw = bitmapMetadata.GetQuery(Pano360PanelNamespace + ":MapZoomLevel");

                if (raw != null && int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int zoom))
                {
                    data.MapZoomLevel = zoom;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Erreur de lecture MapZoomLevel : {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine($"[Metadata] MapZoomLevel : {data.MapZoomLevel}");
        }
        // ─────────────────────────────────────────────────────────────────────
        // LECTURE POSITION SPHERE — XMP namespace personnalisé (pano360panel:SphereUVHome)
        // ─────────────────────────────────────────────────────────────────────
        // Champ propre à ce plugin 
        private static void TentativeLectureSphereHome(BitmapMetadata bitmapMetadata, PanoMetadata data)
        {
            try
            {
                var raw = bitmapMetadata.GetQuery(Pano360PanelNamespace + ":HomeHorizontalSphere");

                if (raw != null && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double horizon))
                {
                    data.HomeHorizontalSphere = horizon;
                }

                var raw2 = bitmapMetadata.GetQuery(Pano360PanelNamespace + ":HomeVerticalSphere");

                if (raw2 != null && double.TryParse(raw2.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double vertical))
                {
                    data.HomeVerticalSphere = vertical;
                }

                var raw3 = bitmapMetadata.GetQuery(Pano360PanelNamespace + ":HomeFovSphere");

                if (raw3 != null && double.TryParse(raw3.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fov))
                {
                    data.HomeFovSphere = fov;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Erreur de lecture SphereHome : {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine($"[Metadata] SphereHome : {data.HomeHorizontalSphere} - {data.HomeVerticalSphere} - {data.HomeFovSphere}");
        }
        // ─────────────────────────────────────────────────────────────────────
        // ÉCRITURE LOCALISATION — helper générique (chemin XMP complet déjà construit par l'appelant)
        // ─────────────────────────────────────────────────────────────────────
        // N'écrit rien si la valeur est nulle/vide, pour ne pas écraser une donnée déjà présente sur le
        // fichier alors que Nominatim n'a simplement pas renvoyé ce champ pour la position actuelle
        // (ex : pas de numéro de rue en zone rurale).
        private static void EcrireChampTexte(BitmapMetadata metadataClone, string cheminXmp, string valeur)
        {
            if (string.IsNullOrEmpty(valeur)) return;
            metadataClone.SetQuery(cheminXmp, valeur);
        }
        // ─────────────────────────────────────────────────────────────────────
        // LECTURE LOCALISATION — schémas XMP standard IPTC Core / photoshop (+ namespace perso en complément)
        // ─────────────────────────────────────────────────────────────────────
        // Lit la localisation textuelle mise en cache lors d'un géocodage inverse Nominatim précédent
        // (cf. MettreAJourLocalisationTexteAsync), pour éviter de réinterroger Nominatim à chaque ouverture
        // d'une photo déjà géolocalisée. Champs écrits dans les schémas standard (compatibles Lightroom,
        // Bridge, ACDSee...) sauf PostCode/County qui restent dans le namespace personnalisé à ce plugin
        // (pas d'équivalent simple côté IPTC Core).
        private static void TentativeLectureLocalisation(BitmapMetadata bitmapMetadata, PanoMetadata data)
        {
            try
            {
                data.LocCity = bitmapMetadata.GetQuery(PhotoshopNamespace + "City")?.ToString();
                data.LocState = bitmapMetadata.GetQuery(PhotoshopNamespace + "State")?.ToString();
                data.LocCountry = bitmapMetadata.GetQuery(PhotoshopNamespace + "Country")?.ToString();
                data.LocCountryCode = bitmapMetadata.GetQuery(PhotoshopNamespace + "CountryCode")?.ToString()
                    ?? bitmapMetadata.GetQuery(Iptc4xmpCoreNamespace + "CountryCode")?.ToString();

                // Sublocation IPTC Core = "numéro + rue" concaténés à l'écriture ; on ne peut pas les
                // re-séparer fiablement à la lecture, donc on récupère tel quel dans LocRoad et on laisse
                // LocHouseNumber vide (il n'est utile qu'au moment de la concaténation initiale).
                data.LocRoad = bitmapMetadata.GetQuery(Iptc4xmpCoreNamespace + "Location")?.ToString();

                data.LocPostCode = bitmapMetadata.GetQuery(Pano360PanelNamespace + ":LocPostCode")?.ToString();
                data.LocCounty = bitmapMetadata.GetQuery(Pano360PanelNamespace + ":LocCounty")?.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Metadata] Erreur de lecture localisation : {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine($"[Metadata] Localisation lue : {data.LocCity} / {data.LocCountry}");
        }
        #endregion Perso
        #region Photographie
        // ─────────────────────────────────────────────────────────────────────
        //                            Photo
        // ═════════════════════════════════════════════════════════════════════
        //                         ExposureTime
        // ─────────────────────────────────────────────────────────────────────
        private static string FormatExposureTime(object value)
        {
            if (value is ulong rational)
            {
                uint numerator = (uint)(rational & 0xFFFFFFFF);
                uint denominator = (uint)(rational >> 32);

                if (denominator == 0)
                    return "--";

                double exposure = (double)numerator / denominator;

                //if (exposure < 1.0)
                //    return $"1/{Math.Round(1.0 / exposure)}";

                // Permet de prendre en compte les valeurs exotiques du type 1/6142 pour devenir 1/6000
                if (exposure < 1.0)
                {
                    double reciprocal = 1.0 / exposure;

                    if (reciprocal >= 1000)
                    {
                        reciprocal = Math.Round(reciprocal / 1000.0) * 1000;
                    }
                    else if (reciprocal >= 100)
                    {
                        reciprocal = Math.Round(reciprocal / 100.0) * 100;
                    }
                    else if (reciprocal >= 10)
                    {
                        reciprocal = Math.Round(reciprocal / 10.0) * 10;
                    }
                    else
                    {
                        reciprocal = Math.Round(reciprocal);
                    }

                    return $"1/{reciprocal:0}";
                }

                return $"{exposure:0.##} s";
            }

            return "--";
        }
        // ─────────────────────────────────────────────────────────────────────
        //                         Date et Heure
        // ─────────────────────────────────────────────────────────────────────
        private static string DateFormatage(string dateTimeRaw)
        {
            DateTime date = DateTime.ParseExact(dateTimeRaw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            //string dateLisible = date.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
            var culture = CultureInfo.GetCultureInfo("fr-FR");
            string dateLisible = char.ToUpper(date.ToString("dddd", culture)[0]) + date.ToString("dddd d MMMM yyyy", culture).Substring(1);
            return dateLisible;
        }
        private static string HeureFormatage(string dateTimeRaw)
        {
            DateTime date = DateTime.ParseExact(dateTimeRaw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            string heure = date.ToString("HH:mm:ss");
            return heure;
        }
        #endregion Photographie
        #region Debogage
        // ─────────────────────────────────────────────────────────────────────
        //                    DÉBOGAGE DES MÉTADONNÉES
        // ─────────────────────────────────────────────────────────────────────
        //
        // Utiliser : PanoMetadataService.DiagnostiquerMetadonnees(filePath);
        public static void DiagnostiquerMetadonnees(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata bitmapMetadata)
                    {
                        System.Diagnostics.Debug.WriteLine($"--- DÉBUT EXPLORATION MÉTADONNÉES DE : {Path.GetFileName(filePath)} ---");
                        ParcourirQueries(bitmapMetadata, "");

                        // Forcer l'exploration du bloc GPS (non listé par l'énumérateur standard WPF)
                        var gpsBlock = bitmapMetadata.GetQuery("/app1/ifd/gps") as BitmapMetadata;
                        if (gpsBlock != null)
                        {
                            System.Diagnostics.Debug.WriteLine("--- BLOC GPS EXIF FORCÉ ---");
                            ParcourirQueries(gpsBlock, "/app1/ifd/gps");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("--- PAS DE BLOC GPS EXIF TROUVÉ ---");
                        }

                        System.Diagnostics.Debug.WriteLine("--- FIN EXPLORATION ---");
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Erreur diag : " + ex.Message); }
        }
        private static void ParcourirQueries(BitmapMetadata metadata, string cheminParent)
        {
            foreach (string relativeQuery in metadata)
            {
                try
                {
                    object valeur = metadata.GetQuery(relativeQuery);
                    string cheminComplet = cheminParent + relativeQuery;

                    if (valeur is BitmapMetadata sousMetadata)
                    {
                        ParcourirQueries(sousMetadata, cheminComplet);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"{cheminComplet} = {valeur} ({valeur?.GetType().Name})");
                    }
                }
                catch
                {
                    // On ignore proprement les tags que WPF ne sait pas décoder
                }
            }
        }
        #endregion Debogage
    }
}