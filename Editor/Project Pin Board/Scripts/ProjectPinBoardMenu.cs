using UnityEditor;

namespace ChenPipi.ProjectPinBoard.Editor
{

    /// <summary>
    /// 菜单
    /// </summary>
    public static class ProjectPinBoardMenu
    {

        private const string k_MenuName = "Project Pin Board 📌";

        #region Menu

        private const string k_AssetsMenuPath = "Tools/Thinhnv/" + k_MenuName + "/";

        private const int k_AssetsMenuPriority = 5;

        private const int k_AssetsMenuPriority2 = 25;

        [MenuItem(k_AssetsMenuPath + "Pin 📌", false, k_AssetsMenuPriority)]
        private static void AssetsMenu_Pin()
        {
            ProjectPinBoardManager.Pin(Selection.assetGUIDs);
        }

        [MenuItem(k_AssetsMenuPath + "Open Window", false, k_AssetsMenuPriority2)]
        private static void AssetsMenu_Open()
        {
            ProjectPinBoardManager.Open();
        }

        [MenuItem(k_AssetsMenuPath + "Reopen Window", false, k_AssetsMenuPriority2)]
        private static void AssetsMenu_Reopen()
        {
            ProjectPinBoardManager.Open(true);
        }

        #endregion

    }

}
