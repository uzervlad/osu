// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Utils;
using osu.Game.Rulesets.Edit;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Screens.Edit;
using osu.Game.Screens.Edit.Compose.Components;
using osu.Game.Utils;
using osuTK;

namespace osu.Game.Rulesets.Osu.Edit
{
    public partial class OsuSelectionScaleHandler : SelectionScaleHandler
    {
        /// <summary>
        /// Whether scaling anchored by the center of the playfield can currently be performed.
        /// </summary>
        public Bindable<bool> CanScaleFromPlayfieldOrigin { get; private set; } = new BindableBool();

        /// <summary>
        /// Whether a single slider is currently selected, which results in a different scaling behaviour.
        /// </summary>
        public Bindable<bool> IsScalingSlider { get; private set; } = new BindableBool();

        [Resolved]
        private IEditorChangeHandler? changeHandler { get; set; }

        [Resolved(CanBeNull = true)]
        private IDistanceSnapProvider? snapProvider { get; set; }

        private BindableList<HitObject> selectedItems { get; } = new BindableList<HitObject>();

        [BackgroundDependencyLoader]
        private void load(EditorBeatmap editorBeatmap)
        {
            selectedItems.BindTo(editorBeatmap.SelectedHitObjects);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            selectedItems.CollectionChanged += (_, __) => updateState();
            updateState();
        }

        private void updateState()
        {
            var quad = GeometryUtils.GetSurroundingQuad(selectedMovableObjects);

            CanScaleX.Value = quad.Width > 0;
            CanScaleY.Value = quad.Height > 0;
            CanScaleDiagonally.Value = CanScaleX.Value && CanScaleY.Value;
            CanScaleFromPlayfieldOrigin.Value = selectedMovableObjects.Any();
            IsScalingSlider.Value = selectedMovableObjects.Count() == 1 && selectedMovableObjects.First() is Slider;
        }

        private Dictionary<OsuHitObject, OriginalHitObjectState>? objectsInScale;
        private Vector2? defaultOrigin;
        private List<Vector2>? originalConvexHull;

        public override void Begin()
        {
            if (OperationInProgress.Value)
                throw new InvalidOperationException($"Cannot {nameof(Begin)} a scale operation while another is in progress!");

            base.Begin();

            changeHandler?.BeginChange();

            objectsInScale = selectedMovableObjects.ToDictionary(ho => ho, ho => new OriginalHitObjectState(ho));
            OriginalSurroundingQuad = objectsInScale.Count == 1 && objectsInScale.First().Key is Slider slider
                ? GeometryUtils.GetSurroundingQuad(slider.Path.ControlPoints.Select(p => slider.Position + p.Position))
                : GeometryUtils.GetSurroundingQuad(objectsInScale.Keys);
            originalConvexHull = objectsInScale.Count == 1 && objectsInScale.First().Key is Slider slider2
                ? GeometryUtils.GetConvexHull(slider2.Path.ControlPoints.Select(p => slider2.Position + p.Position))
                : GeometryUtils.GetConvexHull(objectsInScale.Keys);
            defaultOrigin = GeometryUtils.MinimumEnclosingCircle(originalConvexHull).Item1;
        }

        public override void Update(Vector2 scale, Vector2? origin = null, Axes adjustAxis = Axes.Both, float axisRotation = 0)
        {
            if (!OperationInProgress.Value)
                throw new InvalidOperationException($"Cannot {nameof(Update)} a scale operation without calling {nameof(Begin)} first!");

            Debug.Assert(objectsInScale != null && defaultOrigin != null && OriginalSurroundingQuad != null);

            Vector2 actualOrigin = origin ?? defaultOrigin.Value;
            scale = clampScaleToAdjustAxis(scale, adjustAxis);

            // for the time being, allow resizing of slider paths only if the slider is
            // the only hit object selected. with a group selection, it's likely the user
            // is not looking to change the duration of the slider but expand the whole pattern.
            if (objectsInScale.Count == 1 && objectsInScale.First().Key is Slider slider)
            {
                var originalInfo = objectsInScale[slider];
                Debug.Assert(originalInfo.PathControlPointPositions != null && originalInfo.PathControlPointTypes != null);
                scaleSlider(slider, scale, originalInfo.PathControlPointPositions, originalInfo.PathControlPointTypes, axisRotation);
            }
            else
            {
                scale = ClampScaleToPlayfieldBounds(scale, actualOrigin, adjustAxis, axisRotation);

                foreach (var (ho, originalState) in objectsInScale)
                {
                    ho.Position = GeometryUtils.GetScaledPosition(scale, actualOrigin, originalState.Position, axisRotation);
                }
            }

            moveSelectionInBounds();
        }

        public override void Commit()
        {
            if (!OperationInProgress.Value)
                throw new InvalidOperationException($"Cannot {nameof(Commit)} a rotate operation without calling {nameof(Begin)} first!");

            changeHandler?.EndChange();

            base.Commit();

            objectsInScale = null;
            OriginalSurroundingQuad = null;
            defaultOrigin = null;
        }

        private IEnumerable<OsuHitObject> selectedMovableObjects => selectedItems.Cast<OsuHitObject>()
                                                                                 .Where(h => h is not Spinner);

        private Vector2 clampScaleToAdjustAxis(Vector2 scale, Axes adjustAxis)
        {
            switch (adjustAxis)
            {
                case Axes.Y:
                    scale.X = 1;
                    break;

                case Axes.X:
                    scale.Y = 1;
                    break;

                case Axes.None:
                    scale = Vector2.One;
                    break;
            }

            return scale;
        }

        private void scaleSlider(Slider slider, Vector2 scale, Vector2[] originalPathPositions, PathType?[] originalPathTypes, float axisRotation = 0)
        {
            scale = Vector2.ComponentMax(scale, new Vector2(Precision.FLOAT_EPSILON));

            // Maintain the path types in case they were defaulted to bezier at some point during scaling
            for (int i = 0; i < slider.Path.ControlPoints.Count; i++)
            {
                slider.Path.ControlPoints[i].Position = GeometryUtils.GetScaledPosition(scale, Vector2.Zero, originalPathPositions[i], axisRotation);
                slider.Path.ControlPoints[i].Type = originalPathTypes[i];
            }

            // Snap the slider's length to the current beat divisor
            // to calculate the final resulting duration / bounding box before the final checks.
            slider.SnapTo(snapProvider);

            //if sliderhead or sliderend end up outside playfield, revert scaling.
            Quad scaledQuad = GeometryUtils.GetSurroundingQuad(new OsuHitObject[] { slider });
            (bool xInBounds, bool yInBounds) = isQuadInBounds(scaledQuad);

            if (xInBounds && yInBounds && slider.Path.HasValidLength)
                return;

            for (int i = 0; i < slider.Path.ControlPoints.Count; i++)
                slider.Path.ControlPoints[i].Position = originalPathPositions[i];

            // Snap the slider's length again to undo the potentially-invalid length applied by the previous snap.
            slider.SnapTo(snapProvider);
        }

        private (bool X, bool Y) isQuadInBounds(Quad quad)
        {
            bool xInBounds = (quad.TopLeft.X >= 0) && (quad.BottomRight.X <= OsuPlayfield.BASE_SIZE.X);
            bool yInBounds = (quad.TopLeft.Y >= 0) && (quad.BottomRight.Y <= OsuPlayfield.BASE_SIZE.Y);

            return (xInBounds, yInBounds);
        }

        /// <summary>
        /// Clamp scale for multi-object-scaling where selection does not exceed playfield bounds or flip.
        /// </summary>
        /// <param name="origin">The origin from which the scale operation is performed</param>
        /// <param name="scale">The scale to be clamped</param>
        /// <param name="adjustAxis">The axes to adjust the scale in.</param>
        /// <param name="axisRotation">The rotation of the axes in degrees</param>
        /// <returns>The clamped scale vector</returns>
        public Vector2 ClampScaleToPlayfieldBounds(Vector2 scale, Vector2? origin = null, Axes adjustAxis = Axes.Both, float axisRotation = 0)
        {
            //todo: this is not always correct for selections involving sliders. This approximation assumes each point is scaled independently, but sliderends move with the sliderhead.
            if (objectsInScale == null || adjustAxis == Axes.None)
                return scale;

            Debug.Assert(defaultOrigin != null && OriginalSurroundingQuad != null);

            if (objectsInScale.Count == 1 && objectsInScale.First().Key is Slider slider)
                origin = slider.Position;

            float cos = MathF.Cos(float.DegreesToRadians(-axisRotation));
            float sin = MathF.Sin(float.DegreesToRadians(-axisRotation));
            scale = clampScaleToAdjustAxis(scale, adjustAxis);
            Vector2 actualOrigin = origin ?? defaultOrigin.Value;
            IEnumerable<Vector2> points;

            if (axisRotation == 0)
            {
                var selectionQuad = OriginalSurroundingQuad.Value;
                points = new[]
                {
                    selectionQuad.TopLeft,
                    selectionQuad.TopRight,
                    selectionQuad.BottomLeft,
                    selectionQuad.BottomRight
                };
            }
            else
                points = originalConvexHull!;

            foreach (var point in points)
            {
                scale = clampToBound(scale, point, Vector2.Zero);
                scale = clampToBound(scale, point, OsuPlayfield.BASE_SIZE);
            }

            return Vector2.ComponentMax(scale, new Vector2(Precision.FLOAT_EPSILON));

            float minPositiveComponent(Vector2 v) => MathF.Min(v.X < 0 ? float.PositiveInfinity : v.X, v.Y < 0 ? float.PositiveInfinity : v.Y);

            Vector2 clampToBound(Vector2 s, Vector2 p, Vector2 bound)
            {
                p -= actualOrigin;
                bound -= actualOrigin;
                var a = new Vector2(cos * cos * p.X - sin * cos * p.Y, -sin * cos * p.X + sin * sin * p.Y);
                var b = new Vector2(sin * sin * p.X + sin * cos * p.Y, sin * cos * p.X + cos * cos * p.Y);

                switch (adjustAxis)
                {
                    case Axes.X:
                        s.X = MathF.Min(scale.X, minPositiveComponent(Vector2.Divide(bound - b, a)));
                        break;

                    case Axes.Y:
                        s.Y = MathF.Min(scale.Y, minPositiveComponent(Vector2.Divide(bound - a, b)));
                        break;

                    case Axes.Both:
                        s = Vector2.ComponentMin(s, s * minPositiveComponent(Vector2.Divide(bound, a * s.X + b * s.Y)));
                        break;
                }

                return s;
            }
        }

        private void moveSelectionInBounds()
        {
            Quad quad = GeometryUtils.GetSurroundingQuad(objectsInScale!.Keys);

            Vector2 delta = Vector2.Zero;

            if (quad.TopLeft.X < 0)
                delta.X -= quad.TopLeft.X;
            if (quad.TopLeft.Y < 0)
                delta.Y -= quad.TopLeft.Y;

            if (quad.BottomRight.X > OsuPlayfield.BASE_SIZE.X)
                delta.X -= quad.BottomRight.X - OsuPlayfield.BASE_SIZE.X;
            if (quad.BottomRight.Y > OsuPlayfield.BASE_SIZE.Y)
                delta.Y -= quad.BottomRight.Y - OsuPlayfield.BASE_SIZE.Y;

            foreach (var (h, _) in objectsInScale!)
                h.Position += delta;
        }

        private struct OriginalHitObjectState
        {
            public Vector2 Position { get; }
            public Vector2[]? PathControlPointPositions { get; }
            public PathType?[]? PathControlPointTypes { get; }

            public OriginalHitObjectState(OsuHitObject hitObject)
            {
                Position = hitObject.Position;
                PathControlPointPositions = (hitObject as IHasPath)?.Path.ControlPoints.Select(p => p.Position).ToArray();
                PathControlPointTypes = (hitObject as IHasPath)?.Path.ControlPoints.Select(p => p.Type).ToArray();
            }
        }
    }
}
