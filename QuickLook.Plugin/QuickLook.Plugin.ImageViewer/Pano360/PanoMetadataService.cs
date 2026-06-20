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

        // Dictionnaire évolutif pour stocker d'autres propriétés à la volée
        public Dictionary<string, object> CustomTags { get; set; } = new Dictionary<string, object>();
    }

    public static class PanoMetadataService
    {
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

                        // Rating
                        if (data.Rating.HasValue)
                            metadataClone.SetQuery("/xmp/xmp:Rating", data.Rating.Value.ToString());
                        else if (metadataClone.ContainsQuery("/xmp/xmp:Rating"))
                            metadataClone.RemoveQuery("/xmp/xmp:Rating");

                        // Label
                        if (data.Label != null)
                            metadataClone.SetQuery("/xmp/xmp:Label", data.Label);

                        // GPS (EXIF standard uniquement — jamais XMP DJI, qui reste un format de lecture/fallback)
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

                        // ToDo: A ajouter ?
                        // !! Mots clé (bag XMP dc:subject)
                        // >Normalement OK ?
                        //if (data.CustomTags.TryGetValue("Keywords", out var keywordsObj) && keywordsObj is List<string> motsCles)
                        //{
                        //    // On vide d'abord les entrées existantes, sinon SetQuery se contente
                        //    // d'écraser les index déjà présents et laisse les anciens en trop si
                        //    // la nouvelle liste est plus courte que l'ancienne.
                        //    int indexExistant = 0;
                        //    while (metadataClone.ContainsQuery($"/xmp/dc:subject/{{ulong={indexExistant}}}"))
                        //    {
                        //        metadataClone.RemoveQuery($"/xmp/dc:subject/{{ulong={indexExistant}}}");
                        //        indexExistant++;
                        //    }

                        //    for (int i = 0; i < motsCles.Count; i++)
                        //    {
                        //        metadataClone.SetQuery($"/xmp/dc:subject/{{ulong={i}}}", motsCles[i]);
                        //    }
                        //}

                        // Reconstruction du fichier
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
        // ─────────────────────────────────────────────────────────────────────
        //                            GPS
        // ─────────────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────────────
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
                // Le namespace DJI dans le XMP utilise cette URI comme clé de requête WPF
                // const string djiNs = "/xmp/http\\/:\\/\\/www.dji.com\\/drone-dji\\/1.0\\/";

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
        // Le namespace DJI dans le XMP utilise cette URI comme clé de requête WPF.
        // Partagée entre lecture (TentativeLectureGpsXmpDji) et écriture (mise à jour de cohérence).
        private const string DjiNamespace = "/xmp/http\\/:\\/\\/www.dji.com\\/drone-dji\\/1.0\\/";
        // ─────────────────────────────────────────────────────────────────────
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
    }
}