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
        public string GpsLatitude { get; set; }
        public string GpsLongitude { get; set; }

        // Dictionnaire évolutif pour stocker d'autres propriétés à la volée
        public Dictionary<string, object> CustomTags { get; set; } = new Dictionary<string, object>();
    }

    public static class PanoMetadataService
    {
        // ─────────────────────────────────────────────────────────────────────
        // Deboguage DES MÉTADONNÉES
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
                    // CORRECTION : On interroge avec la requête relative sur le bloc actuel
                    object valeur = metadata.GetQuery(relativeQuery);
                    string cheminComplet = cheminParent + relativeQuery;

                    if (valeur is BitmapMetadata sousMetadata)
                    {
                        // On descend d'un niveau dans l'arbre des métadonnées
                        ParcourirQueries(sousMetadata, cheminComplet);
                    }
                    else
                    {
                        // On affiche le chemin absolu reconstruit et sa vraie valeur
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

                // Utilisation de FileShare.Read pour ne pas bloquer le fichier si l'UI l'utilise
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // DelayCreation permet de ne pas charger les pixels en mémoire RAM, uniquement les en-têtes
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

                    if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata bitmapMetadata)
                    {
                        // ──── 1. Lecture des données XMP

                        // Pour les étoiles
                        if (bitmapMetadata.ContainsQuery("/xmp/xmp:Rating"))
                        {
                            var ratingRaw = bitmapMetadata.GetQuery("/xmp/xmp:Rating");
                            if (ratingRaw != null && short.TryParse(ratingRaw.ToString(), out short r))
                            {
                                data.Rating = r;
                            }
                        }
                        // Pour les couleurs
                        if (bitmapMetadata.ContainsQuery("/xmp/xmp:Label"))
                            data.Label = bitmapMetadata.GetQuery("/xmp/xmp:Label") as string;

                        // ──── 2. Lecture des données EXIF (Requêtes IFD/Exif spécifiques au format JPG)
                        if (bitmapMetadata.ContainsQuery("/app1/ifd/exif/{ushort=34855}")) // Tag ISO
                            data.Iso = bitmapMetadata.GetQuery("/app1/ifd/exif/{ushort=34855}") as ushort?;

                        if (bitmapMetadata.ContainsQuery("/app1/ifd/exif/{ushort=33434}")) // Tag ExposureTime (Vitesse d'obturation)
                            data.ShutterSpeed = bitmapMetadata.GetQuery("/app1/ifd/exif/{ushort=33434}")?.ToString();

                        // Exemple d'extraction générique pour vos futurs tests
                        // Vous pouvez stocker n'allant chercher que la clé brute
                        //if (bitmapMetadata.ContainsQuery("/app1/ifd/gps/"))
                        //{
                        //    // On stocke temporairement l'objet GPS si présent pour vos futurs développements
                        //    data.CustomTags["GPS_Raw"] = bitmapMetadata.GetQuery("/app1/ifd/gps/");
                        //}
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
        // ÉCRITURE DES MÉTADONNÉES (Préparation pour l'étape 3)
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

                        // APPLICATION DES MODIFICATIONS XMP
                        // Pour le rating
                        if (data.Rating.HasValue)
                        {
                            //On passe la valeur en String pour correspondre au type attendu par le fichier
                            metadataClone.SetQuery("/xmp/xmp:Rating", data.Rating.Value.ToString());
                        }
                        else if (metadataClone.ContainsQuery("/xmp/xmp:Rating"))
                        {
                            metadataClone.RemoveQuery("/xmp/xmp:Rating");
                        }

                        // Pour la couleur
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