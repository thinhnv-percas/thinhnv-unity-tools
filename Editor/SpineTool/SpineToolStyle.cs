using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.SpineTool
{
    public class SpineToolStyle
    {
        public static readonly GUIStyle SearchBoxStyle = EditorStyles.toolbarSearchField;

        public static readonly GUIStyle AnimTime = new ()
            { normal = new GUIStyleState { textColor = GetColor("#7a7a7a")}, alignment = TextAnchor.MiddleRight, margin = new RectOffset(5,5,0,0)}; 
        public static readonly GUIStyle AnimName = new ()
            { normal = new GUIStyleState { textColor = Color.white}, alignment = TextAnchor.MiddleLeft, margin = new RectOffset(5,0,0,0)}; 
        public static readonly GUIStyle AnimSelected = new()
            { normal = new GUIStyleState { background = MakeTex(2,2, GetColor("#2c5d87"))} }; 
        public static readonly GUIStyle AnimNameSelected = new ()
            { normal = new GUIStyleState { textColor = Color.white}, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, margin = new RectOffset(5,0,0,0)}; 
        public static readonly GUIStyle BgPopup = new()
            { normal = new GUIStyleState { background = MakeTex(2,2, GetColor("#4a4a4a"))} }; 

        private static Color GetColor(string s)
        {
            Color c = Color.white;
            ColorUtility.TryParseHtmlString(s, out c);
            return c;
        }
        
        private static Texture2D MakeTex( int width, int height, Color col )
        {
            Color[] pix = new Color[width * height];
            for( int i = 0; i < pix.Length; ++i )
            {
                pix[ i ] = col;
            }
            Texture2D result = new Texture2D( width, height );
            result.SetPixels( pix );
            result.Apply();
            return result;
        }
    }
}