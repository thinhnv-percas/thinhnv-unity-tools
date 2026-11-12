using System.Collections.Generic;
using System.Linq;
using Spine;
using Spine.Unity;
using Spine.Unity.Editor;
using UnityEditor;
using UnityEngine;
using AnimationState = Spine.AnimationState;
using Event = UnityEngine.Event;

namespace Thinhnv.UnityTools.SpineTool
{
    public class SpineToolEditor : EditorWindow
    {
        #region Fields

        private const float MinPreviewScale = 0.1f;
        private const float MaxPreviewScale = 5f;
        private const string AnimationTooltip = "Hold Ctrl + click to copy animation name";
        private const string SkinTooltip = "Hold Ctrl + click to copy skin name";

        private SkeletonAnimation _spine;
        private SkeletonGraphic _uiSpine;
        private GameObject _target;
        private Skeleton _skeleton;
        private AnimationState _state;
        private SkeletonDataAsset _dataAsset;
        private Spine.Animation _currentAnim;
        private List<Skin> _currentSkins = new();
        private Spine.Animation[] _animations;
        private Skin[] _skins;
        private bool _isAutoRun;
        private bool _isNeedReload;
        private float _autoRunSpeed = 1f;
        private float _playTime;
        private float _lastTime;

        private Vector2 _animScroll;
        private Vector2 _skinScroll;
        private string _searchAnimation = string.Empty;
        private string _searchAnimationOld;
        private string _searchSkin = string.Empty;
        private string _searchSkinOld;
        private float _panelWidth = 300;
        private Rect _resizeRect;
        private bool _isResize;

        private GameObject _previewObject;
        private PreviewRenderUtility _previewUtility;
        private Bounds _previewBounds;
        private float _previewScale = 1f;

        #endregion

        #region Properties

        private static float CurrentTime => (float)EditorApplication.timeSinceStartup;

        #endregion

        #region Unity Callbacks

        [MenuItem("Tools/Thinhnv/Spine Tool")]
        private static void ShowWindow()
        {
            GetWindow<SpineToolEditor>("Spine Tool").Show();
        }

        private void OnEnable()
        {
            EditorApplication.update -= HandleEditorUpdate;
            EditorApplication.update += HandleEditorUpdate;
            Selection.selectionChanged += SelectionChanged;
            _resizeRect = new Rect(_panelWidth, 0, 5, position.size.y);
        }

        private void OnDisable()
        {
            EditorApplication.update -= HandleEditorUpdate;
            Selection.selectionChanged -= SelectionChanged;
            DestroyPreview();
            if (_previewUtility != null)
                _previewUtility.Cleanup();
            _previewUtility = null;
        }

        private void OnGUI()
        {
            if (Application.isPlaying)
                return;

            GUILayout.BeginHorizontal(SpineToolStyle.BgPopup);
            _isAutoRun = GUILayout.Toggle(_isAutoRun, "Auto Run", GUILayout.ExpandWidth(false));
            var newDataAsset = (SkeletonDataAsset)EditorGUILayout.ObjectField("", _dataAsset, typeof(SkeletonDataAsset), false);
            GUILayout.EndHorizontal();

            if (newDataAsset != null && newDataAsset != _dataAsset)
            {
                if (_target != null && _target != _previewObject)
                    ChangeDataAsset(newDataAsset);
                else
                    PreviewDataAsset(newDataAsset);
            }

            if (_skeleton == null)
                return;

            GUILayout.BeginVertical(SpineToolStyle.BgPopup);
            GUILayout.Label("Frame: " + (int)(_playTime * 30));
            if (_isAutoRun)
            {
                GUILayout.Label("Speed");
                _autoRunSpeed = EditorGUILayout.Slider(_autoRunSpeed, 0, 1.5f);
            }
            _playTime = EditorGUILayout.Slider(_playTime, 0, _currentAnim != null ? _currentAnim.Duration : 0);
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(_panelWidth));
            DrawSkins();
            DrawAnimations();
            GUILayout.EndVertical();
            if (_previewObject != null)
                DrawPreview();
            GUILayout.EndHorizontal();
            ResizePanel();

            if (_isNeedReload)
                Reload();
        }

        #endregion

        #region Private Methods

        private void SelectionChanged()
        {
            var dataAsset = Selection.activeObject as SkeletonDataAsset;
            if (dataAsset != null)
                PreviewDataAsset(dataAsset);
            else
                LoadTarget(Selection.activeGameObject);
            Repaint();
        }

        private void HandleEditorUpdate()
        {
            var dataAsset = _spine != null ? _spine.skeletonDataAsset : _uiSpine != null ? _uiSpine.skeletonDataAsset : null;
            if (dataAsset != null && dataAsset != _dataAsset)
                ChangeDataAsset(dataAsset);

            if (_currentAnim == null || _state == null)
                return;

            var track = _state.GetCurrent(0);
            if (track == null)
                return;

            _state.TimeScale = _autoRunSpeed;
            if (_isAutoRun)
            {
                _state.Update(CurrentTime - _lastTime);
                if (track.TrackTime >= _currentAnim.Duration)
                    track.TrackTime = 0;
                _playTime = track.TrackTime;
                Repaint();
            }
            else
            {
                track.TrackTime = _playTime;
                _state.Update(0);
            }
            _lastTime = CurrentTime;

            if (_spine != null)
            {
                _spine.LateUpdate();
                _spine.Skeleton.UpdateWorldTransform();
                return;
            }

            if (_uiSpine == null)
                return;

            _uiSpine.LateUpdate();
            _uiSpine.Skeleton.UpdateWorldTransform();
            _uiSpine.UpdateMesh();
        }

        private void LoadTarget(GameObject target)
        {
            if (target == null)
                return;

            var spine = target.GetComponent<SkeletonAnimation>();
            var uiSpine = spine == null ? target.GetComponent<SkeletonGraphic>() : null;
            if (spine == null && (uiSpine == null || uiSpine == _uiSpine))
                return;

            var skeleton = spine != null ? spine.Skeleton : uiSpine.Skeleton;
            if (skeleton == null)
                return;

            if (target != _previewObject)
                DestroyPreview();

            _spine = spine;
            _uiSpine = uiSpine;
            _target = target;
            _skeleton = skeleton;
            _state = spine != null ? spine.AnimationState : uiSpine.AnimationState;
            _dataAsset = spine != null ? spine.skeletonDataAsset : uiSpine.skeletonDataAsset;
            _currentAnim = skeleton.Data.Animations.Count > 0 ? skeleton.Data.Animations.Items[0] : null;
            _currentSkins.Clear();
            _searchAnimationOld = null;
            _searchSkinOld = null;

            var skinName = skeleton.Data.Skins.Items[0].Name;
            if (spine != null)
                spine.initialSkinName = skinName;
            else
                uiSpine.initialSkinName = skinName;
            skeleton.SetSkin(skinName);
            _isNeedReload = true;
        }

        private void Reload()
        {
            _isNeedReload = false;
            if (_spine != null)
            {
                _spine.ClearState();
                if (_currentAnim != null)
                    _spine.AnimationName = _currentAnim.Name;
                _spine.initialSkinName = _skeleton.Data.Skins.Items[0].Name;
                _spine.Initialize(true);
                _state = _spine.AnimationState;
            }
            else
            {
                _uiSpine.Clear();
                if (_currentAnim != null)
                    _uiSpine.startingAnimation = _currentAnim.Name;
                _uiSpine.Initialize(true);
                _state = _uiSpine.AnimationState;
            }

            _skeleton = _spine != null ? _spine.Skeleton : _uiSpine.Skeleton;
            _state.TimeScale = _autoRunSpeed;
            _target.SetActive(false);
            _target.SetActive(true);
            ApplySkins();
            Repaint();
        }

        private void ChangeDataAsset(SkeletonDataAsset dataAsset)
        {
            var data = dataAsset.GetSkeletonData(true);
            if (data == null)
                return;

            Undo.RecordObject(_spine != null ? (Object)_spine : _uiSpine, "Change Skeleton Data Asset");
            if (_spine != null)
            {
                _spine.skeletonDataAsset = dataAsset;
                _spine.initialSkinName = data.Skins.Items[0].Name;
                _spine.Initialize(true);
            }
            else
            {
                _uiSpine.skeletonDataAsset = dataAsset;
                _uiSpine.initialSkinName = data.Skins.Items[0].Name;
                _uiSpine.Initialize(true);
                _uiSpine = null;
            }

            LoadTarget(_target);
            Repaint();
        }

        private void PreviewDataAsset(SkeletonDataAsset dataAsset)
        {
            if (dataAsset.GetSkeletonData(true) == null)
                return;

            DestroyPreview();
            if (_previewUtility == null)
            {
                _previewUtility = new PreviewRenderUtility(true);
                var camera = _previewUtility.camera;
                camera.orthographic = true;
                camera.cullingMask = -1;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 1000f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            }

            var preview = SkeletonAnimation.NewSkeletonAnimationGameObject(dataAsset);
            _previewObject = preview.gameObject;
            _previewObject.name = dataAsset.name;
            _previewObject.hideFlags = HideFlags.HideAndDontSave;
            _previewUtility.AddSingleGO(_previewObject);
            LoadTarget(_previewObject);
            preview.LateUpdate();
            _previewBounds = preview.GetComponent<MeshRenderer>().bounds;
            _previewScale = 1f;
            _previewUtility.camera.transform.SetPositionAndRotation(_previewBounds.center + new Vector3(0, 0, -10f), Quaternion.identity);
        }

        private void DestroyPreview()
        {
            if (_previewObject != null)
                DestroyImmediate(_previewObject);
        }

        private void DrawPreview()
        {
            var rect = GUILayoutUtility.GetRect(200, 10000, 200, 10000);
            if (Event.current.type == EventType.ScrollWheel && rect.Contains(Event.current.mousePosition))
            {
                _previewScale = Mathf.Clamp(_previewScale * (1f - Event.current.delta.y * 0.05f), MinPreviewScale, MaxPreviewScale);
                Event.current.Use();
                Repaint();
            }

            if (Event.current.type == EventType.Repaint)
            {
                _previewUtility.camera.orthographicSize = Mathf.Max(_previewBounds.extents.y, _previewBounds.extents.x * rect.height / rect.width) * 1.1f / _previewScale;
                _previewUtility.BeginPreview(rect, GUIStyle.none);
                _previewUtility.camera.Render();
                GUI.DrawTexture(rect, _previewUtility.EndPreview(), ScaleMode.StretchToFill, false);
            }

            _previewScale = EditorGUI.Slider(new Rect(rect.xMax - 210, rect.yMax - 24, 200, 18), _previewScale, MinPreviewScale, MaxPreviewScale);
        }

        private void DrawAnimations()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Animations", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            _searchAnimation = GUILayout.TextField(_searchAnimation, SpineToolStyle.SearchBoxStyle);
            GUILayout.EndHorizontal();

            if (_searchAnimation != _searchAnimationOld)
            {
                _searchAnimationOld = _searchAnimation;
                _animations = _skeleton.Data.Animations.Items.Where(a => a.Name.ToLower().Contains(_searchAnimation.ToLower())).ToArray();
            }

            _animScroll = GUILayout.BeginScrollView(_animScroll);
            foreach (var animation in _animations)
            {
                var isSelected = animation == _currentAnim;
                GUILayout.BeginHorizontal(isSelected ? SpineToolStyle.AnimSelected : GUIStyle.none);
                GUILayout.Label(new GUIContent(SpineEditorUtilities.Icons.animation, AnimationTooltip), GUILayout.ExpandWidth(false));
                GUILayout.Label(animation.Name, isSelected ? SpineToolStyle.AnimNameSelected : SpineToolStyle.AnimName, GUILayout.Height(24));
                GUILayout.Label(animation.Duration.ToString(), SpineToolStyle.AnimTime, GUILayout.ExpandWidth(false), GUILayout.Height(24));
                GUILayout.EndHorizontal();

                if (!IsClicked(GUILayoutUtility.GetLastRect()))
                    continue;

                _currentAnim = animation;
                _isNeedReload = true;
                if (Event.current.control)
                    EditorGUIUtility.systemCopyBuffer = animation.Name;
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawSkins()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MaxHeight(150));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Skins", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            _searchSkin = GUILayout.TextField(_searchSkin, SpineToolStyle.SearchBoxStyle);
            GUILayout.EndHorizontal();

            if (_searchSkin != _searchSkinOld)
            {
                _searchSkinOld = _searchSkin;
                _skins = _skeleton.Data.Skins.Items.Where(s => s.Name.ToLower().Contains(_searchSkin.ToLower())).ToArray();
            }

            var activeSkin = _skeleton.Skin ?? _skeleton.Data.DefaultSkin;
            _skinScroll = GUILayout.BeginScrollView(_skinScroll);
            foreach (var skin in _skins)
            {
                var isSelected = _currentSkins.Count > 0 ? _currentSkins.Contains(skin) : activeSkin == skin;
                GUILayout.BeginHorizontal(isSelected ? SpineToolStyle.AnimSelected : GUIStyle.none);
                GUILayout.Label(new GUIContent(SpineEditorUtilities.Icons.skin, SkinTooltip), GUILayout.ExpandWidth(false));
                GUILayout.Label(skin.Name, isSelected ? SpineToolStyle.AnimNameSelected : SpineToolStyle.AnimName, GUILayout.Height(24));
                GUILayout.EndHorizontal();

                if (!IsClicked(GUILayoutUtility.GetLastRect()))
                    continue;

                if (!_currentSkins.Remove(skin))
                    _currentSkins.Add(skin);
                ApplySkins();
                if (Event.current.control)
                    EditorGUIUtility.systemCopyBuffer = skin.Name;
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private bool IsClicked(Rect rect)
        {
            return Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition);
        }

        private void ApplySkins()
        {
            if (_currentSkins.Count == 0)
                return;

            var skin = new Skin("run");
            foreach (var item in _currentSkins)
                skin.AddSkin(item);

            _skeleton.SetSkin(skin);
            _skeleton.SetSlotsToSetupPose();
            if (_spine != null)
            {
                _spine.initialSkinName = skin.Name;
                _spine.LateUpdate();
                _spine.AnimationState.Apply(_skeleton);
                return;
            }

            _uiSpine.LateUpdate();
        }

        private void ResizePanel()
        {
            EditorGUIUtility.AddCursorRect(_resizeRect, MouseCursor.ResizeHorizontal);
            if (IsClicked(_resizeRect))
                _isResize = true;
            if (Event.current.type == EventType.MouseUp)
                _isResize = false;
            if (!_isResize)
                return;

            _panelWidth = Mathf.Clamp(Event.current.mousePosition.x, 100, position.width - 100);
            _resizeRect.x = _panelWidth;
            Repaint();
        }

        #endregion
    }
}
