using CountersPlus.Custom;
using HMUI;
using PPPredictor.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using static PPPredictor.Core.DataType.Enums;

namespace PPPredictor.Counter
{
    internal sealed class PPPCounterPreview : ICounterPreview
    {
        public void Render(CounterPreviewContext preview)
        {
            var profile = Plugin.ProfileInfo;
            var boards = new List<Leaderboard>();
            if (profile.IsScoreSaberEnabled) boards.Add(Leaderboard.ScoreSaber);
            if (profile.IsBeatLeaderEnabled) boards.Add(Leaderboard.BeatLeader);
            if (profile.IsHitBloqEnabled) boards.Add(Leaderboard.HitBloq);
            if (profile.IsAccSaberEnabled) boards.Add(Leaderboard.AccSaber);
            if (boards.Count == 0) boards.Add(Leaderboard.ScoreSaber);

            Canvas canvas = preview.CanvasUtility.GetCanvasFromID(preview.Settings.CanvasID);
            float positionScale = preview.CanvasUtility.GetCanvasSettingsFromID(preview.Settings.CanvasID).PositionScale;
            float lineOffset = 0.15f * (boards.Count / 2 + boards.Count % 2);
            foreach (Leaderboard board in boards)
            {
                RenderRow(preview, canvas, positionScale, lineOffset, board);
                lineOffset -= 0.3f;
            }
        }

        private static void RenderRow(CounterPreviewContext preview, Canvas canvas, float positionScale, float lineOffset, Leaderboard board)
        {
            var profile = Plugin.ProfileInfo;
            float factor = 10 / positionScale;
            float centerOffset = CenterOffset(profile.CounterDisplayType);
            float iconTextOffset = profile.CounterUseIcons ? -0.9f : 0;
            CounterDisplayType mode = profile.CounterDisplayType;
            float displayOffset = 0;
            if (mode == CounterDisplayType.PPNoSuffix || mode == CounterDisplayType.PPAndGainNoSuffix ||
                mode == CounterDisplayType.PPAndGainNoBracketsNoSuffix || mode == CounterDisplayType.GainNoBrackets)
                displayOffset = -0.2f;
            else if (mode == CounterDisplayType.GainNoBracketsNoSuffix)
                displayOffset = -0.4f;
            float y = lineOffset * factor;

            if (profile.CounterUseIcons)
                AddIcon(preview, canvas, board, positionScale, centerOffset, y);
            else
            {
                TMP_Text header = preview.CreateText();
                header.rectTransform.anchoredPosition += new Vector2((-1f + centerOffset) * factor, y) * positionScale;
                header.alignment = TextAlignmentOptions.BottomLeft;
                header.fontSize = 3;
                header.text = $"<color=\"{DisplayHelper.GetDisplayColor(0, false)}\">{board}</color>";
            }

            float valueX = 0.9f + iconTextOffset + displayOffset + centerOffset;
            float gainX = 1.2f + iconTextOffset + displayOffset + centerOffset;
            string suffix = mode == CounterDisplayType.PPNoSuffix || mode == CounterDisplayType.PPAndGainNoSuffix ||
                mode == CounterDisplayType.PPAndGainNoBracketsNoSuffix || mode == CounterDisplayType.GainNoBracketsNoSuffix ? "" : "pp";
            bool showPP = mode != CounterDisplayType.GainNoBrackets && mode != CounterDisplayType.GainNoBracketsNoSuffix;
            bool showGain = mode == CounterDisplayType.PPAndGain || mode == CounterDisplayType.PPAndGainNoSuffix ||
                mode == CounterDisplayType.PPAndGainNoBrackets || mode == CounterDisplayType.PPAndGainNoBracketsNoSuffix ||
                mode == CounterDisplayType.GainNoBrackets || mode == CounterDisplayType.GainNoBracketsNoSuffix;

            if (showPP)
            {
                TMP_Text pp = preview.CreateText();
                pp.rectTransform.anchoredPosition += new Vector2(valueX * factor, y) * positionScale;
                pp.alignment = TextAlignmentOptions.BottomRight;
                pp.fontSize = 3;
                pp.text = $"123.45{suffix}";
            }
            if (showGain)
            {
                TMP_Text gain = preview.CreateText();
                gain.rectTransform.anchoredPosition += new Vector2(gainX * factor, y) * positionScale;
                gain.alignment = mode == CounterDisplayType.GainNoBrackets || mode == CounterDisplayType.GainNoBracketsNoSuffix
                    ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.BottomLeft;
                gain.fontSize = 3;
                string value = $"<color=\"{DisplayHelper.GetDisplayColor(4.2, false, true)}\">4.20{suffix}</color>";
                bool brackets = mode == CounterDisplayType.PPAndGain || mode == CounterDisplayType.PPAndGainNoSuffix;
                gain.text = brackets ? $"[{value}]" : value;
            }
        }

        private static float CenterOffset(CounterDisplayType mode)
        {
            switch (mode)
            {
                case CounterDisplayType.PP: return 0.5f;
                case CounterDisplayType.PPNoSuffix: return 0.6f;
                case CounterDisplayType.PPAndGainNoSuffix:
                case CounterDisplayType.PPAndGainNoBracketsNoSuffix: return 0.3f;
                case CounterDisplayType.GainNoBrackets: return 0.4f;
                case CounterDisplayType.GainNoBracketsNoSuffix: return 0.6f;
                default: return 0;
            }
        }

        private static void AddIcon(CounterPreviewContext preview, Canvas canvas, Leaderboard board, float positionScale, float centerOffset, float y)
        {
            string resource = $"PPPredictor.Resources.LeaderBoardLogos.{board}.png";
            byte[] data = ImagePreparationWorker.ReadPreviewResource(resource);
            if (data == null) return;

            Texture2D texture = new Texture2D(1, 1);
            preview.Track(texture);
            if (!texture.LoadImage(data)) return;
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.zero);
            preview.Track(sprite);

            GameObject imageObject = new GameObject(resource, typeof(RectTransform));
            preview.Track(imageObject);
            ImageView image = imageObject.AddComponent<ImageView>();
            image.rectTransform.SetParent(canvas.transform, false);
            image.rectTransform.anchoredPosition = positionScale * preview.CanvasUtility.GetAnchoredPositionFromConfig(preview.Settings) +
                new Vector3((-1f + centerOffset) * 10, y * positionScale + 1.35f, 0);
            image.rectTransform.sizeDelta = new Vector2(2.5f, 2.5f);
            image.material = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(material => material.name == "UINoGlow");
            image.sprite = sprite;
            image.enabled = true;
        }
    }
}
