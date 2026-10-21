using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Streplay
{
    // Where the main menu's empty name slot (the recessed bar next to the back arrow, "lobbyname") is on screen, so the
    // Replays button can sit inside it like part of the frame. The frame is a 3D model drawn by a camera into a render
    // texture that a RawImage shows full screen; the slot's bounds go through that camera's viewport and that image.
    // Null when the slot isn't up (title screen, in a lobby where the slot shows the lobby name, or a game update
    // renamed things); the browser then falls back to a plain button.
    internal static class MenuSlot
    {
        static Renderer _slot;
        static Camera _cam;
        static RawImage _image;
        static float _nextLookup;

        public static Rect? ScreenRect()
        {
            if (!Find()) return null;
            if (!_slot.enabled || !_slot.gameObject.activeInHierarchy || !_cam.isActiveAndEnabled) return null;
            if (_image != null && !_image.isActiveAndEnabled) return null;

            var b = _slot.bounds;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var v = _cam.WorldToViewportPoint(c);
                if (v.z <= 0f) return null;
                x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x);
                y0 = Mathf.Min(y0, v.y); y1 = Mathf.Max(y1, v.y);
            }
            // the viewport maps onto the image showing the render texture (full screen when there isn't one)
            Rect shown = new Rect(0f, 0f, Screen.width, Screen.height);
            if (_image != null)
            {
                var corners = new Vector3[4];
                _image.rectTransform.GetWorldCorners(corners); // overlay canvas: world = screen pixels
                shown = Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            }
            float sx0 = shown.x + x0 * shown.width, sx1 = shown.x + x1 * shown.width;
            float sy0 = shown.y + y0 * shown.height, sy1 = shown.y + y1 * shown.height;
            var box = Rect.MinMaxRect(sx0, Screen.height - sy1, sx1, Screen.height - sy0); // GUI: y down
            // The bounds include the slot's bevel and depth; the dark recess itself is this part of them (measured on
            // the 1.4.9 frame, proportional, so it holds at any resolution).
            var r = Rect.MinMaxRect(box.x + box.width * 0.075f, box.y + box.height * 0.45f,
                                    box.x + box.width * 0.955f, box.y + box.height * 0.78f);
            // anything odd (off screen, absurd size) means the frame isn't what we expect: use the fallback button
            bool sane = r.width > 20f && r.height > 8f && r.xMin >= 0f && r.yMin >= 0f && r.xMax <= Screen.width && r.yMax <= Screen.height
                        && r.width < Screen.width * 0.6f && r.height < Screen.height * 0.2f;
            return sane ? r : (Rect?)null;
        }

        internal static string Diag()
        {
            bool found = Find();
            return $"found={found} slot={(_slot ? $"{_slot.name} enabled={_slot.enabled} active={_slot.gameObject.activeInHierarchy} bounds={_slot.bounds}" : "null")} " +
                   $"cam={(_cam ? $"{_cam.name} active={_cam.isActiveAndEnabled} rt={(_cam.targetTexture ? _cam.targetTexture.name : "screen")}" : "null")} " +
                   $"image={(_image ? $"{Recorder.ScenePath(_image.transform)} active={_image.isActiveAndEnabled}" : "null")} rect={ScreenRect()}";
        }

        // The game keeps more than one copy of the frame (the options menu has its own, hidden), so only the copy that's
        // on screen counts; the lookup is redone (at most once a second) whenever the cached one isn't.
        static bool Live() =>
            _slot != null && _slot.gameObject.activeInHierarchy && _cam != null && _cam.isActiveAndEnabled
            && (_image == null || _image.isActiveAndEnabled);

        static bool Find()
        {
            if (Live()) return true;
            if (Time.unscaledTime < _nextLookup) return false;
            _nextLookup = Time.unscaledTime + 1f;
            _slot = null; _cam = null; _image = null;
            foreach (var slot in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (slot == null || slot.name != "lobbyname" || !slot.gameObject.scene.IsValid() || !slot.gameObject.activeInHierarchy) continue;
                int layer = 1 << slot.gameObject.layer;
                // the frame's own camera renders it into a texture that's shown full screen; another camera can share the
                // layer (an orthographic one elsewhere), so the texture camera wins
                var cam = Resources.FindObjectsOfTypeAll<Camera>()
                    .Where(c => c != null && c.isActiveAndEnabled && (c.cullingMask & layer) != 0)
                    .OrderByDescending(c => c.targetTexture != null)
                    .FirstOrDefault();
                if (cam == null) continue;
                _image = cam.targetTexture == null ? null : Resources.FindObjectsOfTypeAll<RawImage>()
                    .FirstOrDefault(i => i != null && i.isActiveAndEnabled && i.texture == cam.targetTexture);
                _slot = slot; _cam = cam;
                return true;
            }
            return false;
        }
    }
}
