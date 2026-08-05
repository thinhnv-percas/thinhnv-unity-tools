using Autodesk.Fbx;

namespace Percas.UnityTools.Fbx
{
    /// <summary>
    /// Stateless helpers for mutating an FbxNode's transform, pivot, name and
    /// place in the hierarchy. Callers are expected to log every call through
    /// FbxDocument.RecordChange so the pre-save diff and undo stack stay accurate.
    /// </summary>
    public static class FbxNodeOperations
    {
        public static string GetNodePath(FbxNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parent = node.GetParent();
            if (parent == null || string.IsNullOrEmpty(parent.GetName()))
            {
                return node.GetName();
            }

            return GetNodePath(parent) + "/" + node.GetName();
        }

        public static bool IsBoneOrSkinned(FbxNode node)
        {
            var attribute = node.GetNodeAttribute();
            if (attribute == null)
            {
                return false;
            }

            if (attribute.GetAttributeType() == FbxNodeAttribute.EType.eSkeleton)
            {
                return true;
            }

            if (attribute.GetAttributeType() == FbxNodeAttribute.EType.eMesh)
            {
                var mesh = (FbxMesh)attribute;
                return mesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin) > 0;
            }

            return false;
        }

        public static void Rename(FbxNode node, string newName)
        {
            node.SetName(newName);
        }

        public static FbxDouble3 GetLocalTranslation(FbxNode node) => node.LclTranslation.Get();
        public static FbxDouble3 GetLocalRotation(FbxNode node) => node.LclRotation.Get();
        public static FbxDouble3 GetLocalScaling(FbxNode node) => node.LclScaling.Get();

        public static void SetLocalTranslation(FbxNode node, FbxDouble3 value) => node.LclTranslation.Set(value);
        public static void SetLocalRotation(FbxNode node, FbxDouble3 value) => node.LclRotation.Set(value);
        public static void SetLocalScaling(FbxNode node, FbxDouble3 value) => node.LclScaling.Set(value);

        public static FbxVector4 GetRotationPivot(FbxNode node) =>
            node.GetRotationPivot(FbxNode.EPivotSet.eSourcePivot);

        public static void SetRotationPivot(FbxNode node, FbxVector4 value) =>
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, value);

        public static FbxVector4 GetScalingPivot(FbxNode node) =>
            node.GetScalingPivot(FbxNode.EPivotSet.eSourcePivot);

        public static void SetScalingPivot(FbxNode node, FbxVector4 value) =>
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, value);

        public static FbxVector4 GetGeometricTranslation(FbxNode node) =>
            node.GetGeometricTranslation(FbxNode.EPivotSet.eSourcePivot);

        public static void SetGeometricTranslation(FbxNode node, FbxVector4 value) =>
            node.SetGeometricTranslation(FbxNode.EPivotSet.eSourcePivot, value);

        /// <summary>
        /// Moves a node under a new parent. When preserveWorldTransform is true,
        /// the node's local transform is recomputed from its current global
        /// transform so it does not visually jump — reparenting keeps local
        /// values as-is otherwise, same as Unity's own Transform.SetParent(false).
        /// </summary>
        public static void Reparent(FbxScene scene, FbxNode node, FbxNode newParent, bool preserveWorldTransform)
        {
            FbxAMatrix globalBefore = default;
            if (preserveWorldTransform)
            {
                globalBefore = scene.GetEvaluator().GetNodeGlobalTransform(node);
            }

            node.GetParent()?.RemoveChild(node);
            newParent.AddChild(node);

            if (!preserveWorldTransform)
            {
                return;
            }

            var newParentGlobal = scene.GetEvaluator().GetNodeGlobalTransform(newParent);
            var newLocal = newParentGlobal.Inverse() * globalBefore;

            node.LclTranslation.Set(ToDouble3(newLocal.GetT()));
            node.LclRotation.Set(ToDouble3(newLocal.GetR()));
            node.LclScaling.Set(ToDouble3(newLocal.GetS()));
        }

        public static void Delete(FbxNode node)
        {
            node.GetParent()?.RemoveChild(node);
            node.Destroy();
        }

        private static FbxDouble3 ToDouble3(FbxVector4 v) => new FbxDouble3(v.X, v.Y, v.Z);
    }
}
