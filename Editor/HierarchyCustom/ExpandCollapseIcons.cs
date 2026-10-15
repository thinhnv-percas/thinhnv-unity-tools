using UnityEngine;

public static class ExpandCollapseIcons
{
    public static Texture2D CreateExpandTexture()
    {
        return CreateIcon(
            topUp: true,
            bottomUp: false
        );
    }

    public static Texture2D CreateCollapseTexture()
    {
        return CreateIcon(
            topUp: false,
            bottomUp: true
        );
    }

    private static Texture2D CreateIcon(bool topUp, bool bottomUp)
    {
        const int size = 32;

        Texture2D tex = new Texture2D(
            size,
            size,
            TextureFormat.RGBA32,
            false
        );

        tex.name = topUp ? "ExpandIcon" : "CollapseIcon";
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        Color32 transparent = new Color32(0, 0, 0, 0);
        Color32 white = new Color32(230, 230, 230, 255);

        Color32[] pixels = new Color32[size * size];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = transparent;

        tex.SetPixels32(pixels);

        // Nhỏ hơn + khoảng cách giữa 2 mũi tên lớn hơn
        DrawChevron(tex, 16, 23, topUp, white);
        DrawChevron(tex, 16, 9, bottomUp, white);

        tex.Apply();

        return tex;
    }

    private static void DrawChevron(
        Texture2D tex,
        int centerX,
        int centerY,
        bool up,
        Color32 color)
    {
        // Nhỏ hơn
        const int width = 6;
        const int height = 3;

        // Nét mỏng hơn
        const int thickness = 2;

        if (up)
        {
            //   /\
            //  /  \
            DrawLine(
                tex,
                centerX - width,
                centerY - height,
                centerX,
                centerY + height,
                thickness,
                color
            );

            DrawLine(
                tex,
                centerX,
                centerY + height,
                centerX + width,
                centerY - height,
                thickness,
                color
            );
        }
        else
        {
            //  \  /
            //   \/
            DrawLine(
                tex,
                centerX - width,
                centerY + height,
                centerX,
                centerY - height,
                thickness,
                color
            );

            DrawLine(
                tex,
                centerX,
                centerY - height,
                centerX + width,
                centerY + height,
                thickness,
                color
            );
        }
    }

    private static void DrawLine(
        Texture2D tex,
        int x0,
        int y0,
        int x1,
        int y1,
        int thickness,
        Color32 color)
    {
        int dx = x1 - x0;
        int dy = y1 - y0;

        int steps = Mathf.Max(
            Mathf.Abs(dx),
            Mathf.Abs(dy)
        );

        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 0 : (float)i / steps;

            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));

            for (int ox = -thickness; ox <= thickness; ox++)
            {
                for (int oy = -thickness; oy <= thickness; oy++)
                {
                    if (ox * ox + oy * oy <= thickness * thickness)
                    {
                        int px = x + ox;
                        int py = y + oy;

                        if (px >= 0 && px < tex.width &&
                            py >= 0 && py < tex.height)
                        {
                            tex.SetPixel(px, py, color);
                        }
                    }
                }
            }
        }
    }
}