using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.ImageViewer.Pano360
{
    public class PanoMetadata
    {
        // Données XMP
        public short? Rating { get; set; }
        public string Label { get; set; }
        // Données EXIF
        public ushort? Iso { get; set; }
        public string ShutterSpeed { get; set; }
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
        // DÉBOGAGE DES MÉTADONNÉES
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
                            data.Label = bitmapMetadata.GetQuery("/xmp/xmp:Label") as string;

                        // ──── 2. Lecture des données EXIF ────────────────────────────────────

                        // ISO
                        if (bitmapMetadata.ContainsQuery("/app1/ifd/exif/{ushort=34855}"))
                            data.Iso = bitmapMetadata.GetQuery("/app1/ifd/exif/{ushort=34855}") as ushort?;

                        // Vitesse d'obturation (ExposureTime)
                        if (bitmapMetadata.ContainsQuery("/app1/ifd/exif/{ushort=33434}"))
                            data.ShutterSpeed = bitmapMetadata.GetQuery("/app1/ifd/exif/{ushort=33434}")?.ToString();

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
                const string djiNs = "/xmp/http\\/:\\/\\/www.dji.com\\/drone-dji\\/1.0\\/";

                var latStr = bitmapMetadata.GetQuery(djiNs + ":GpsLatitude") as string;
                var lonStr = bitmapMetadata.GetQuery(djiNs + ":GpsLongitude") as string;
                var altStr = bitmapMetadata.GetQuery(djiNs + ":AbsoluteAltitude") as string;

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

        /// <summary>
        /// Convertit un tableau DMS EXIF (3 rationnels UInt64) en degrés décimaux signés.
        /// Chaque UInt64 encode : numérateur dans les 32 bits hauts, dénominateur dans les 32 bits bas.
        /// </summary>
        private static string ConvertirGpsDms(object rawValue, string reference)
        {
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

        /// <summary>
        /// Décode un rationnel EXIF encodé en UInt64 (numérateur << 32 | dénominateur).
        /// </summary>
        private static double DecodeRationnel(ulong rationnel)
        {
            uint numerateur = (uint)(rationnel >> 32);
            uint denominateur = (uint)(rationnel & 0xFFFFFFFF);
            return denominateur == 0 ? 0.0 : (double)numerateur / denominateur;
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
    }
}