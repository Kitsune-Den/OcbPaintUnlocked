using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

// Compatibility with Custom Avatars (by Alter).
//
// Custom Avatars builds the thumbnails for its avatar list once, at the main
// menu, as GPU-only copies (Graphics.CopyTexture into a new Texture2D). When
// the texture mipmap limit or mipmap streaming is switched, Unity uploads
// textures again from their CPU side, and for those copies the CPU side was
// never filled in. They come back as a flat mustard yellow.
//
// We switch exactly those settings for a moment while patching the paint
// atlas (see OcbTextureUtils.ResetQualitySettings), so afterwards we ask
// Custom Avatars to copy its thumbnails again. It has a coroutine for that,
// which it runs itself whenever the video options are saved.
//
// Everything here is looked up by reflection, so nothing happens when
// Custom Avatars is not installed or a later version renames things.
public static class CompatCustomAvatars
{

    // Wait for our own texture work to go quiet before refreshing,
    // so a burst of atlas patches leads to one refresh, not several
    const float SettleSeconds = 1f;

    static bool Searched = false;
    static MethodInfo UpdateThumbnails = null;

    static bool Pending = false;
    static float LastRequest = 0f;

    // Call after quality settings were switched and restored
    public static void RequestThumbnailRefresh()
    {
        if (GameManager.IsDedicatedServer) return;
        if (!FindCustomAvatars()) return;
        LastRequest = Time.realtimeSinceStartup;
        if (Pending) return;
        var manager = GameManager.Instance;
        if (manager == null) return;
        Pending = true;
        manager.StartCoroutine(RefreshWhenSettled());
    }

    static bool FindCustomAvatars()
    {
        if (Searched) return UpdateThumbnails != null;
        Searched = true;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("CustomAvatarsList", false);
                if (type == null) continue;
                var method = type.GetMethod("UpdateThumbnails",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (method == null || !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
                {
                    Log.Warning("[OcbCustomTextures] Custom Avatars found, but not its thumbnail refresh. " +
                        "Its avatar thumbnails may turn yellow after a world loads.");
                    return false;
                }
                UpdateThumbnails = method;
                Log.Out("[OcbCustomTextures] Custom Avatars found: its avatar thumbnails are refreshed after texture patching");
                return true;
            }
        }
        catch (Exception err)
        {
            Log.Warning("[OcbCustomTextures] Looking for Custom Avatars failed: " + err.Message);
        }
        return false;
    }

    static IEnumerator RefreshWhenSettled()
    {
        while (Time.realtimeSinceStartup - LastRequest < SettleSeconds)
            yield return null;
        // Requests arriving from here on start a new refresh
        Pending = false;
        IEnumerator refresh = null;
        try
        {
            refresh = UpdateThumbnails.Invoke(null, null) as IEnumerator;
        }
        catch (Exception err)
        {
            Log.Warning("[OcbCustomTextures] Custom Avatars thumbnail refresh did not start: " + Describe(err));
        }
        if (refresh == null) yield break;
        // Step through their coroutine ourselves, so an error inside
        // it is reported once here and does not surface as ours
        while (true)
        {
            object step = null;
            bool more = false;
            bool failed = false;
            try
            {
                more = refresh.MoveNext();
                if (more) step = refresh.Current;
            }
            catch (Exception err)
            {
                failed = true;
                Log.Warning("[OcbCustomTextures] Custom Avatars thumbnail refresh stopped: " + Describe(err));
            }
            if (failed || !more) break;
            yield return step;
        }
    }

    static string Describe(Exception err)
    {
        if (err is TargetInvocationException && err.InnerException != null)
            err = err.InnerException;
        return err.GetType().Name + ": " + err.Message;
    }

}
