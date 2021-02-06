using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Deform;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace DeformEditor
{
    [CustomEditor(typeof(LatticeDeformer))]
    public class LatticeDeformerEditor : DeformerEditor
    {
        private Vector3Int newResolution;

        HashSet<int> selectedIndices = new HashSet<int>();

        private static class Content
        {
            public static readonly GUIContent Target = new GUIContent(text: "Target", tooltip: DeformEditorGUIUtility.Strings.AxisTooltip);
            public static readonly GUIContent Resolution = new GUIContent(text: "Resolution", tooltip: "Per axis control point counts, the higher the resolution the more splits");
        }

        private class Properties
        {
            public SerializedProperty Target;
            public SerializedProperty Resolution;

            public Properties(SerializedObject obj)
            {
                Target = obj.FindProperty("target");
                Resolution = obj.FindProperty("resolution");
            }
        }

        private Properties properties;

        protected override void OnEnable()
        {
            base.OnEnable();

            properties = new Properties(serializedObject);
            newResolution = properties.Resolution.vector3IntValue;
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            
            LatticeDeformer latticeDeformer = ((LatticeDeformer) target);

            serializedObject.UpdateIfRequiredOrScript();

            EditorGUILayout.PropertyField(properties.Target, Content.Target);

            newResolution = EditorGUILayout.Vector3IntField(Content.Resolution, newResolution);
            // Make sure we have at least two control points per axis
            newResolution = Vector3Int.Max(newResolution, new Vector3Int(2, 2, 2));
            // Don't let the lattice resolution get ridiculously high
            newResolution = Vector3Int.Min(newResolution, new Vector3Int(32, 32, 32));

            if (GUILayout.Button("Update Lattice"))
            {
                Undo.RecordObject(target, "Update Lattice");
                latticeDeformer.GenerateControlPoints(newResolution, true);
                selectedIndices.Clear();
            }

            if (GUILayout.Button("Reset Lattice Points"))
            {
                Undo.RecordObject(target, "Reset Lattice Points");
                latticeDeformer.GenerateControlPoints(newResolution);
                selectedIndices.Clear();
            }

            if(latticeDeformer.CanAutoFitBounds)
            {
                if (GUILayout.Button("Auto-Fit Bounds"))
                {
                    Undo.RecordObject(target, "Auto-Fit Bounds");
                    latticeDeformer.FitBoundsToParentDeformable();
                }
            }

            serializedObject.ApplyModifiedProperties();

            EditorApplication.QueuePlayerLoopUpdate();
        }

        public override void OnSceneGUI()
        {
            base.OnSceneGUI();

            var lattice = target as LatticeDeformer;
            var controlPoints = lattice.ControlPoints;

            using (new Handles.DrawingScope(lattice.transform.localToWorldMatrix))
            {
                var cachedZTest = Handles.zTest;

                // Change the depth testing to only show handles in front of solid objects (i.e. typical depth testing) 
                Handles.zTest = CompareFunction.LessEqual;
                DrawLattice(lattice, DeformHandles.LineMode.Solid);
                // Change the depth testing to only show handles *behind* solid objects 
                Handles.zTest = CompareFunction.Greater;
                DrawLattice(lattice, DeformHandles.LineMode.Light);

                // Restore the original z test value now we're done with our drawing
                Handles.zTest = cachedZTest;

                var resolution = lattice.Resolution;
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            var controlPointHandleID = GUIUtility.GetControlID("LatticeDeformerControlPoint".GetHashCode(), FocusType.Passive);
                            var activeColor = DeformEditorSettings.SolidHandleColor;
                            var controlPointIndex = lattice.GetIndex(x, y, z);

                            if (GUIUtility.hotControl == controlPointHandleID || selectedIndices.Contains(controlPointIndex))
                            {
                                activeColor = Handles.selectedColor;
                            }
                            else if (HandleUtility.nearestControl == controlPointHandleID)
                            {
                                activeColor = Handles.preselectionColor;
                            }

                            Event e = Event.current;
                            if (e.type == EventType.MouseDown && HandleUtility.nearestControl == controlPointHandleID && e.button == 0)
                            {
                                Debug.Log(controlPointHandleID);
                                GUIUtility.hotControl = controlPointHandleID;
                                GUIUtility.keyboardControl = controlPointHandleID;
                                e.Use();

                                bool modifierKeyPressed = (e.modifiers & EventModifiers.Control) != 0
                                                          || (e.modifiers & EventModifiers.Shift) != 0
                                                          || (e.modifiers & EventModifiers.Command) != 0;

                                if (modifierKeyPressed && selectedIndices.Contains(controlPointIndex))
                                {
                                    // Pressed a modifier key so toggle the selection
                                    selectedIndices.Remove(controlPointIndex);
                                }
                                else
                                {
                                    if (!modifierKeyPressed)
                                    {
                                        selectedIndices.Clear();
                                    }

                                    selectedIndices.Add(controlPointIndex);
                                    if (selectedIndices.Count == 1) // Is this our first selection?
                                    {
                                        // Make sure when we start selecting control points we don't have a transform tool active as it'll be confusing having multiple ones
                                        Tools.current = Tool.None;
                                    }
                                }
                            }

                            if (Tools.current != Tool.None && selectedIndices.Count != 0)
                            {
                                // The user has changed to a tool but we have control points selected, so clear the selection
                                selectedIndices.Clear();
                            }

                            using (new Handles.DrawingScope(activeColor))
                            {
                                var position = controlPoints[controlPointIndex];
                                var size = HandleUtility.GetHandleSize(position) * DeformEditorSettings.ScreenspaceLatticeHandleCapSize;

                                Handles.DotHandleCap(
                                    controlPointHandleID,
                                    position,
                                    Quaternion.identity,
                                    size,
                                    Event.current.type);
                            }
                        }
                    }
                }
            }

            if (selectedIndices.Count != 0)
            {
                // Get the average position
                var position = float3.zero;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    foreach (var index in selectedIndices)
                    {
                        position += controlPoints[index];
                    }

                    position /= selectedIndices.Count;
                }
                else
                {
                    position = controlPoints[selectedIndices.Last()];
                }

                position = lattice.Target.TransformPoint(position);

                EditorGUI.BeginChangeCheck();
                var rotation = lattice.Target.rotation;
                if (Tools.pivotRotation == PivotRotation.Global)
                {
                    rotation = Quaternion.identity;
                }

                float3 newPosition = Handles.PositionHandle(position, rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(target, "Update Lattice");

                    var delta = newPosition - position;
                    delta = lattice.Target.InverseTransformVector(delta);
                    foreach (var selectedIndex in selectedIndices)
                    {
                        controlPoints[selectedIndex] += delta;
                    }
                }
            }
            
            // If the lattice is visible, override Unity's built-in Select All so that it selects all control points 
            if (Event.current.type == EventType.ExecuteCommand && Event.current.commandName == "SelectAll")
            {
                selectedIndices.Clear();
                var resolution = lattice.Resolution;
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            var controlPointIndex = lattice.GetIndex(x, y, z);
                            selectedIndices.Add(controlPointIndex);
                        }
                    }
                }

                Event.current.Use();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void DrawLattice(LatticeDeformer lattice, DeformHandles.LineMode lineMode)
        {
            var resolution = lattice.Resolution;
            var controlPoints = lattice.ControlPoints;
            for (int z = 0; z < resolution.z - 1; z++)
            {
                for (int y = 0; y < resolution.y - 1; y++)
                {
                    for (int x = 0; x < resolution.x - 1; x++)
                    {
                        int index000 = lattice.GetIndex(x, y, z);
                        int index100 = lattice.GetIndex(x + 1, y, z);
                        int index010 = lattice.GetIndex(x, y + 1, z);
                        int index110 = lattice.GetIndex(x + 1, y + 1, z);
                        int index001 = lattice.GetIndex(x, y, z + 1);
                        int index101 = lattice.GetIndex(x + 1, y, z + 1);
                        int index011 = lattice.GetIndex(x, y + 1, z + 1);
                        int index111 = lattice.GetIndex(x + 1, y + 1, z + 1);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index100], lineMode);
                        DeformHandles.Line(controlPoints[index010], controlPoints[index110], lineMode);
                        DeformHandles.Line(controlPoints[index001], controlPoints[index101], lineMode);
                        DeformHandles.Line(controlPoints[index011], controlPoints[index111], lineMode);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index010], lineMode);
                        DeformHandles.Line(controlPoints[index100], controlPoints[index110], lineMode);
                        DeformHandles.Line(controlPoints[index001], controlPoints[index011], lineMode);
                        DeformHandles.Line(controlPoints[index101], controlPoints[index111], lineMode);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index001], lineMode);
                        DeformHandles.Line(controlPoints[index100], controlPoints[index101], lineMode);
                        DeformHandles.Line(controlPoints[index010], controlPoints[index011], lineMode);
                        DeformHandles.Line(controlPoints[index110], controlPoints[index111], lineMode);
                    }
                }
            }
        }
    }
}