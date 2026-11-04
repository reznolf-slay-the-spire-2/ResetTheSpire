using System;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace ResetTheSpire.ResetTheSpireCode;

/// <summary>
/// The game looks for a mod's preview image at <c>res://&lt;modId&gt;/mod_image.png</c>, which only
/// resolves when the mod ships a Godot .pck. This mod is distributed as loose files, so fall back
/// to loading <c>mod_image.png</c> from the mod folder for the modding screen's info panel.
/// </summary>
[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ModInfoFillPatch
{
    private static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        try
        {
            TextureRect? image = __instance.GetNodeOrNull<TextureRect>("ModImage");
            if (image == null || image.Texture != null)
            {
                return;
            }

            string path = Path.Combine(mod.path, "mod_image.png");
            if (!File.Exists(path))
            {
                return;
            }

            var png = new Image();
            Error error = png.LoadPngFromBuffer(File.ReadAllBytes(path));
            if (error != Error.Ok)
            {
                MainFile.Logger.Warn($"Could not decode '{path}': {error}");
                return;
            }

            image.Texture = ImageTexture.CreateFromImage(png);
        }
        catch (Exception e)
        {
            // A missing preview image must never break the modding screen.
            MainFile.Logger.Warn($"Could not load the mod image: {e}");
        }
    }
}
