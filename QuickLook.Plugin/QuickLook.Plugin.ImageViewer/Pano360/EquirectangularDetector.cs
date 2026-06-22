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

namespace QuickLook.Plugin.ImageViewer.Pano360;

/// <summary>
/// Détecte si une image est une panoramique équirectangulaire
/// en vérifiant que son ratio largeur/hauteur est proche de 2:1.
/// </summary>
public static class EquirectangularDetector
{
    /// <summary>
    /// Tolérance autour du ratio 2:1 (ex: 0.05 = ±5%).
    /// Une image de 7952×3976 a un ratio de 1.999... → détectée.
    /// Une image de 1920×1080 a un ratio de 1.777... → non détectée.
    /// </summary>
    private const double Tolerance = 0.05;

    /// <summary>
    /// Taille minimale en pixels pour éviter les faux positifs
    /// sur de petites images qui auraient accidentellement un ratio 2:1.
    /// </summary>
    private const int MinimumWidth = 512;

    /// <summary>
    /// Indique si l'image au chemin donné est probablement équirectangulaire.
    /// Utilise les métadonnées déjà chargées par le MetaProvider de QuickLook
    /// pour éviter de relire le fichier.
    /// </summary>
    /// <param name="meta">Le MetaProvider déjà instancié par Plugin.Prepare()</param>
    /// <returns>True si l'image semble être une panoramique équirectangulaire</returns>
    
    public static bool IsEquirectangular(MetaProvider meta)
    {
        Size size = meta.GetSize();

        if (size.IsEmpty || size.Height <= 0 || size.Width < MinimumWidth)
            return false;

        double ratio = size.Width / size.Height;

        return Math.Abs(ratio - 2.0) <= Tolerance;
    }
}
