using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using com.jutsugames.operator112.managers;
using com.jutsugames.operator112.units;
using com.jutsugames.operator112.gamelogic;
using com.jutsugames.operator112.gamelogic.incidents;
using com.jutsugames.operator112.gamelogic.calls;
using com.jutsugames.operator112.gamelogic.onsite;
using com.jutsugames.operator112.UI.menu.districtsView;
using com.jutsugames.operator112.UI.management;
using com.jutsugames.operator112.units.unitStates;
using LocalizationEditor;

namespace Operator112Accessibility
{
    [BepInPlugin("com.jwood.112operator.accessibility", "112 Operator Accessibility", "0.0.23")]
    public sealed class AccessibilityPlugin : BaseUnityPlugin
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("nvdaControllerClient.dll")]
        private static extern int nvdaController_testIfRunning();

        [DllImport("nvdaControllerClient.dll")]
        private static extern int nvdaController_cancelSpeech();

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_speakText(string text);

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_brailleMessage(string text);

        private readonly List<Button> buttons = new List<Button>();
        private bool nvdaLoaded;
        private int buttonIndex = -1;
        private string lastSelection = String.Empty;
        private DateTime announceAtUtc;
        private DateTime nextRefreshUtc;
        private int unitIndex = -1;
        private Unit selectedUnit;
        private int incidentIndex = -1;
        private readonly List<DistrictDataTag> districts = new List<DistrictDataTag>();
        private int districtIndex = -1;
        private bool wasNewIncidentAlert = false;
        private bool wasNewCallAlert = false;
        private readonly Dictionary<int, string> unitTasks = new Dictionary<int, string>();
        private readonly Dictionary<string, DateTime> recentUnitAnnouncements = new Dictionary<string, DateTime>();
        private readonly HashSet<int> knownIncidentIds = new HashSet<int>();
        private readonly Dictionary<int, int> incidentSceneCounts = new Dictionary<int, int>();
        private bool incidentTrackingInitialised = false;
        private bool pauseCheckPending;
        private bool pauseStateBeforeKey;
        private int pauseCheckFramesRemaining;
        private bool wasStartingAreaScreen;
        private readonly Dictionary<int, IncidentOperationalState> sceneOperationalStates = new Dictionary<int, IncidentOperationalState>();
        private GameMode lastGameMode = GameMode.UNSET;
        private bool gameModeTrackingInitialised;
        private bool wasDutySummaryVisible;
        private int dutyResultIndex = -1;

        private struct IncidentOperationalState
        {
            public bool PoliceNeeded;
            public bool MedicalNeeded;
            public bool FireNeeded;
            public bool Fight;
            public int PoliceTransports;
            public int MedicalTransports;
        }

        private void Awake()
        {
            try
            {
                string pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string clientPath = Path.Combine(pluginDirectory, "nvdaControllerClient.dll");
                nvdaLoaded = File.Exists(clientPath) && LoadLibrary(clientPath) != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("NVDA client initialization failed: " + ex.Message);
            }
            announceAtUtc = DateTime.UtcNow.AddSeconds(3);
            nextRefreshUtc = DateTime.MinValue;
            Logger.LogInfo("112 Operator Accessibility loaded.");
        }

        private void Update()
        {
            CheckPendingPauseAnnouncement();
            DateTime now = DateTime.UtcNow;
            if (announceAtUtc != DateTime.MinValue && now >= announceAtUtc)
            {
                announceAtUtc = DateTime.MinValue;
                Speak("112 Operator Accessibility is loaded. Use up and down arrows to browse buttons, Enter to activate, and F1 to repeat.");
            }
            if (now >= nextRefreshUtc)
            {
                nextRefreshUtc = now.AddMilliseconds(400);
                RefreshButtons();
                AnnounceExternalSelection();
                CheckNewAlerts();
                CheckUnitStates();
                CheckIncidentChanges();
                CheckIncidentOperationalChanges();
                CheckGameModeChange();
                CheckStartingAreaGuidance();
                CheckDutySummaryGuidance();
            }
            if (Input.GetKeyDown(KeyCode.F1))
                SpeakCurrent();
            if (Input.GetKeyDown(KeyCode.F2))
                SpeakNextUnit(ShiftHeld() ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.F3))
                SpeakNextIncident(ShiftHeld() ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.F4))
                SpeakNextDistrict(ShiftHeld() ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.F5))
                ChooseCurrentDistrict();
            if (Input.GetKeyDown(KeyCode.F6))
                OpenNewIncident();
            if (Input.GetKeyDown(KeyCode.F7))
                AnswerNewCall();
            if (Input.GetKeyDown(KeyCode.F8))
                DispatchSelected();
            if (Input.GetKeyDown(KeyCode.F9))
                SpeakDutySummary();
            if (Input.GetKeyDown(KeyCode.Space))
            {
                pauseStateBeforeKey = TimeController.Paused;
                pauseCheckPending = true;
                pauseCheckFramesRemaining = 3;
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                ActivateCurrent();
            if (Input.GetKeyDown(KeyCode.DownArrow))
                Move(1);
            else if (Input.GetKeyDown(KeyCode.UpArrow))
                Move(-1);
            // Space is deliberately left to the game's pause/unpause binding.
            // 112's nonstandard screens often do not route Enter through Unity's
            // submit handler, so Enter explicitly activates the browsed button.
        }

        private void CheckPendingPauseAnnouncement()
        {
            if (!pauseCheckPending)
                return;
            bool paused = TimeController.Paused;
            if (paused != pauseStateBeforeKey)
            {
                pauseCheckPending = false;
                Speak(paused ? "Paused." : "Resumed.");
                return;
            }
            pauseCheckFramesRemaining--;
            if (pauseCheckFramesRemaining <= 0)
                pauseCheckPending = false;
        }

        private void ActivateCurrent()
        {
            RefreshButtons();
            Button button = CurrentButton();
            if (button == null)
            {
                Speak("No active menu button is selected.");
                return;
            }
            string description = Describe(button);
            Logger.LogInfo("Explicitly activating: " + description);
            button.onClick.Invoke();
        }

        private void RefreshButtons()
        {
            buttons.Clear();
            CallWindow activeCall = FindPickedUpCallWindow();
            Button[] found = Resources.FindObjectsOfTypeAll<Button>();
            for (int i = 0; i < found.Length; i++)
            {
                Button button = found[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable)
                    continue;
                if (!button.gameObject.scene.IsValid())
                    continue;
                if (!IsEffectivelyVisible(button.transform))
                    continue;
                if (activeCall != null && !IsCallControl(button, activeCall))
                    continue;
                buttons.Add(button);
            }
            buttons.Sort(delegate(Button a, Button b)
            {
                return String.Compare(HierarchyPath(a.transform), HierarchyPath(b.transform), StringComparison.Ordinal);
            });
            if (buttons.Count == 0)
                buttonIndex = -1;
            else if (buttonIndex < 0 || buttonIndex >= buttons.Count)
                buttonIndex = 0;
        }

        private static CallWindow FindPickedUpCallWindow()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.displayedConversation == null)
                return null;
            CallWindow[] windows = Resources.FindObjectsOfTypeAll<CallWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                CallWindow window = windows[i];
                if (window != null && window.gameObject.scene.IsValid() && window.gameObject.activeInHierarchy && window.callActive)
                    return window;
            }
            return null;
        }

        private static bool IsCallControl(Button button, CallWindow callWindow)
        {
            if (button == null || callWindow == null)
                return false;
            if (callWindow.endCallBtn == button)
                return true;
            return callWindow.dialogueOptions != null && button.transform.IsChildOf(callWindow.dialogueOptions);
        }

        private void Move(int direction)
        {
            RefreshButtons();
            if (buttons.Count == 0)
            {
                Speak("No active menu buttons found.");
                return;
            }
            buttonIndex = (buttonIndex + direction + buttons.Count) % buttons.Count;
            Button button = buttons[buttonIndex];
            button.Select();
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(button.gameObject);
            lastSelection = button.GetInstanceID().ToString();
            Speak(Describe(button));
        }

        private void SpeakCurrent()
        {
            RefreshButtons();
            Button button = CurrentButton();
            Speak(button == null ? "No active menu button is selected." : Describe(button));
        }

        private Button CurrentButton()
        {
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (selected != null)
            {
                Button selectedButton = selected.GetComponent<Button>();
                int selectedIndex = buttons.IndexOf(selectedButton);
                if (selectedIndex >= 0)
                {
                    buttonIndex = selectedIndex;
                    return selectedButton;
                }
            }
            return buttonIndex >= 0 && buttonIndex < buttons.Count ? buttons[buttonIndex] : null;
        }

        private void AnnounceExternalSelection()
        {
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (selected == null)
                return;
            Button button = selected.GetComponent<Button>();
            if (button == null || !button.gameObject.activeInHierarchy)
                return;
            if (!buttons.Contains(button))
                return;
            string id = button.GetInstanceID().ToString();
            if (id == lastSelection)
                return;
            lastSelection = id;
            Speak(Describe(button));
        }

        private static string Describe(Button button)
        {
            // The in-duty gear button shares a hierarchy with inactive
            // management-screen text. Proximity lookup can otherwise call it
            // Start, causing players to leave an active duty unintentionally.
            if (button != null && String.Equals(button.gameObject.name, "ManagementButton", StringComparison.OrdinalIgnoreCase))
                return "Management, button";
            StringBuilder result = new StringBuilder();
            AppendTexts(result, button.transform);
            if (result.Length > 180)
            {
                result.Length = 0;
                AppendText(result, NearestText(button.transform, button.transform), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            string stateOnlyLabel = result.ToString().Trim();
            if (IsStateOnlyLabel(stateOnlyLabel))
            {
                Transform contextScope = button.transform.parent;
                // State captions are often nested several levels away from the
                // row label. Continue through the containing canvas and use
                // screen-space proximity to find the matching visible caption.
                for (int depth = 0; depth < 8 && contextScope != null; depth++, contextScope = contextScope.parent)
                {
                    string context = NearestContextText(button.transform, contextScope, stateOnlyLabel);
                    if (!String.IsNullOrWhiteSpace(context))
                    {
                        result.Length = 0;
                        result.Append(context.Trim());
                        result.Append(", ");
                        result.Append(stateOnlyLabel);
                        break;
                    }
                    if (contextScope.GetComponent<Canvas>() != null)
                        break;
                }
            }
            if (result.Length == 0 || IsGenericLabel(result.ToString(), button.gameObject.name))
            {
                // Several 112 menus use an empty image named Background as the
                // actual Button, with the meaningful label beside it under a
                // shared row. Walk upward only until the nearest useful text is
                // found so we do not read the entire dialog for every control.
                Transform ancestor = button.transform.parent;
                for (int depth = 0; depth < 3 && ancestor != null; depth++, ancestor = ancestor.parent)
                {
                    StringBuilder nearby = new StringBuilder();
                    AppendText(nearby, NearestText(button.transform, ancestor), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (nearby.Length > 0 && !IsGenericLabel(nearby.ToString(), button.gameObject.name))
                    {
                        result = nearby;
                        break;
                    }
                }
            }
            if (result.Length == 0)
                result.Append(FriendlyObjectName(button.gameObject.name));
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager != null && manager.GameMode == GameMode.DUTY &&
                String.Equals(result.ToString().Trim(), "START", StringComparison.OrdinalIgnoreCase))
            {
                result.Length = 0;
                result.Append("Management");
            }
            AppendPredefinedUnitAvailability(result, button);
            return result.ToString() + ", button";
        }

        private static void AppendPredefinedUnitAvailability(StringBuilder result, Button button)
        {
            PredefinedSquadLabel label = button == null ? null : button.GetComponentInParent<PredefinedSquadLabel>();
            if (label == null || label.Element == null)
                return;
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            int money = manager == null || manager.levelData == null ? 0 : manager.levelData.Money;
            int price = label.Element.Price;
            bool enoughMoney = money >= price;
            bool canBuy = false;
            try
            {
                ManagementController controller = UnityEngine.Object.FindObjectOfType<ManagementController>();
                canBuy = controller != null && controller.CanBuy(label.Element, label.teamMembers);
            }
            catch
            {
                canBuy = false;
            }
            result.Append(". Price ");
            result.Append(price);
            result.Append(". Funds ");
            result.Append(money);
            if (canBuy)
                result.Append(". Affordable");
            else if (!enoughMoney)
                result.Append(". Cannot buy, insufficient funds");
            else if (!label.buyable)
                result.Append(". Cannot buy, required staff or equipment unavailable");
            else
                result.Append(". Cannot buy on this screen");
        }

        private void CheckGameModeChange()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null)
            {
                gameModeTrackingInitialised = false;
                lastGameMode = GameMode.UNSET;
                return;
            }
            GameMode current = manager.GameMode;
            if (!gameModeTrackingInitialised)
            {
                lastGameMode = current;
                gameModeTrackingInitialised = true;
                return;
            }
            if (current == lastGameMode)
                return;
            GameMode previous = lastGameMode;
            lastGameMode = current;
            if (current == GameMode.DUTY)
                Speak("Duty started. Units are active and incidents can now arrive.");
            else if (current == GameMode.MANAGEMENT && previous == GameMode.DUTY)
                Speak("Management mode. Duty time and incident generation are stopped. Browse to Start and press Enter to begin the duty again.");
        }

        private static void AppendTexts(StringBuilder result, Transform root)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TMP_Text[] tmpTexts = root.gameObject.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmpTexts.Length; i++)
                AppendText(result, ReadableTmpText(tmpTexts[i]), seen);
            Text[] legacyTexts = root.gameObject.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < legacyTexts.Length; i++)
                AppendText(result, legacyTexts[i] == null ? null : legacyTexts[i].text, seen);
        }

        private static bool IsGenericLabel(string text, string objectName)
        {
            string value = (text ?? String.Empty).Trim();
            return value.Length == 0 || value.Equals("Background", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(objectName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStateOnlyLabel(string value)
        {
            return String.Equals(value, "On", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "Off", StringComparison.OrdinalIgnoreCase);
        }

        private static string NearestContextText(Transform target, Transform scope, string stateLabel)
        {
            string best = null;
            float bestDistance = Single.MaxValue;
            TMP_Text[] tmp = scope.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmp.Length; i++)
            {
                string readable = ReadableTmpText(tmp[i]);
                if (tmp[i] == null || !tmp[i].gameObject.activeInHierarchy || String.IsNullOrWhiteSpace(readable) ||
                    IsPlaceholderText(readable) || String.Equals(readable.Trim(), stateLabel, StringComparison.OrdinalIgnoreCase))
                    continue;
                float distance = (tmp[i].transform.position - target.position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = readable; }
            }
            Text[] legacy = scope.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < legacy.Length; i++)
            {
                string readable = legacy[i] == null ? null : legacy[i].text;
                if (legacy[i] == null || !legacy[i].gameObject.activeInHierarchy || String.IsNullOrWhiteSpace(readable) ||
                    IsPlaceholderText(readable) || String.Equals(readable.Trim(), stateLabel, StringComparison.OrdinalIgnoreCase))
                    continue;
                float distance = (legacy[i].transform.position - target.position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = readable; }
            }
            return best;
        }

        private static void AppendText(StringBuilder destination, string value, HashSet<string> seen)
        {
            if (String.IsNullOrEmpty(value))
                return;
            value = value.Trim();
            if (value.Length == 0)
                return;
            value = value.Replace("\n", ". ").Replace("\r", " ");
            if (IsPlaceholderText(value))
                return;
            if (!seen.Add(value))
                return;
            if (destination.Length > 0)
                destination.Append(". ");
            destination.Append(value);
        }

        private static string FriendlyObjectName(string value)
        {
            if (String.Equals(value, "ManagementButton", StringComparison.OrdinalIgnoreCase))
                return "Management";
            return SplitIdentifier(value);
        }

        private static string ReadableTmpText(TMP_Text text)
        {
            if (text == null)
                return null;
            try
            {
                string parsed = text.GetParsedText();
                if (!String.IsNullOrWhiteSpace(parsed))
                    return parsed;
            }
            catch
            {
                // Some inactive TMP objects have not generated their text mesh
                // yet. Their source text is still a safe fallback.
            }
            return text.text;
        }

        private static bool IsPlaceholderText(string value)
        {
            if (value.IndexOf("Lorem ipsum", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            int letters = 0;
            char repeated = '\0';
            bool same = true;
            for (int i = 0; i < value.Length; i++)
            {
                if (!Char.IsLetter(value[i]))
                    continue;
                letters++;
                if (repeated == '\0') repeated = Char.ToLowerInvariant(value[i]);
                else if (Char.ToLowerInvariant(value[i]) != repeated) same = false;
            }
            return letters >= 12 && same;
        }

        private static string NearestText(Transform target, Transform scope)
        {
            string best = null;
            float bestDistance = Single.MaxValue;
            TMP_Text[] tmp = scope.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmp.Length; i++)
            {
                string readable = ReadableTmpText(tmp[i]);
                if (tmp[i] == null || !tmp[i].gameObject.activeInHierarchy || String.IsNullOrWhiteSpace(readable) || IsPlaceholderText(readable))
                    continue;
                float distance = (tmp[i].transform.position - target.position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = readable; }
            }
            Text[] legacy = scope.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < legacy.Length; i++)
            {
                if (legacy[i] == null || !legacy[i].gameObject.activeInHierarchy || String.IsNullOrWhiteSpace(legacy[i].text) || IsPlaceholderText(legacy[i].text))
                    continue;
                float distance = (legacy[i].transform.position - target.position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = legacy[i].text; }
            }
            return best;
        }

        private static bool IsEffectivelyVisible(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                CanvasGroup group = current.GetComponent<CanvasGroup>();
                if (group != null && (group.alpha < 0.05f || !group.interactable || !group.blocksRaycasts))
                    return false;
                current = current.parent;
            }
            RectTransform rect = transform as RectTransform;
            if (rect != null)
            {
                Vector3[] corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                bool entirelyOutside = corners[2].x < 0 || corners[0].x > Screen.width || corners[2].y < 0 || corners[0].y > Screen.height;
                if (entirelyOutside)
                    return false;
            }
            return true;
        }

        private static bool ShiftHeld()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }

        private void SpeakNextUnit(int direction)
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.unitList == null || manager.unitList.Count == 0)
            {
                unitIndex = -1;
                Speak("No active units found.");
                return;
            }
            List<Unit> browseUnits = manager.unitList.Where(u => u != null).ToList();
            browseUnits.Sort(delegate(Unit a, Unit b)
            {
                int numberCompare = GetUnitSortNumber(a).CompareTo(GetUnitSortNumber(b));
                if (numberCompare != 0)
                    return numberCompare;
                string aName = a.Squad == null ? String.Empty : a.Squad.Name;
                string bName = b.Squad == null ? String.Empty : b.Squad.Name;
                return String.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
            });
            if (browseUnits.Count == 0)
            {
                unitIndex = -1;
                selectedUnit = null;
                Speak("No active units found.");
                return;
            }
            int previousIndex = selectedUnit == null ? -1 : browseUnits.IndexOf(selectedUnit);
            unitIndex = previousIndex < 0 ? (direction > 0 ? 0 : browseUnits.Count - 1) :
                (previousIndex + direction + browseUnits.Count) % browseUnits.Count;
            Unit unit = browseUnits[unitIndex];
            selectedUnit = unit;
            string name = unit.Squad == null ? "unnamed unit" : unit.Squad.Name;
            string type = unit.Squad == null ? "unknown type" : DescribeUnitType(unit.Squad.Type.ToString());
            Speak("Unit " + (unitIndex + 1) + " of " + browseUnits.Count + ". " + name + ", " + type + ", " + DescribeUnitActivity(unit) + ".");
        }

        private static int GetUnitSortNumber(Unit unit)
        {
            string name = unit == null || unit.Squad == null ? null : unit.Squad.Name;
            if (String.IsNullOrWhiteSpace(name))
                return Int32.MaxValue;
            int end = name.Length - 1;
            while (end >= 0 && Char.IsWhiteSpace(name[end])) end--;
            int start = end;
            while (start >= 0 && Char.IsDigit(name[start])) start--;
            if (start == end)
                return Int32.MaxValue;
            int number;
            return Int32.TryParse(name.Substring(start + 1, end - start), out number) ? number : Int32.MaxValue;
        }

        private void SpeakNextIncident(int direction)
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            // The DutySummary hierarchy remains active behind normal gameplay.  DUTY must
            // always browse the live incident list, regardless of that hidden hierarchy.
            DutySummary dutySummary = manager != null && manager.GameMode == GameMode.DUTY ? null : FindActiveDutySummary();
            if (dutySummary != null)
            {
                SpeakNextDutyResult(dutySummary, direction);
                return;
            }
            if (manager == null || manager.incidentList == null || manager.incidentList.Count == 0)
            {
                incidentIndex = -1;
                Speak("No active incidents found.");
                return;
            }
            incidentIndex = incidentIndex < 0 ? (direction > 0 ? 0 : manager.incidentList.Count - 1) :
                (incidentIndex + direction + manager.incidentList.Count) % manager.incidentList.Count;
            Incident incident = manager.incidentList[incidentIndex];
            if (incident == null)
            {
                Speak("Incident " + (incidentIndex + 1) + " is unavailable.");
                return;
            }
            string name = GetIncidentName(incident);
            int scenes = incident.scenes == null ? 0 : incident.scenes.Count;
            Speak("Incident " + (incidentIndex + 1) + " of " + manager.incidentList.Count + ". " + name + ", " + scenes + " active scene" + (scenes == 1 ? String.Empty : "s") + ". " + DescribeIncidentOperations(incident));
        }

        private static DutySummary FindActiveDutySummary()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager != null && manager.GameMode == GameMode.DUTY)
                return null;
            DutySummary[] summaries = Resources.FindObjectsOfTypeAll<DutySummary>();
            for (int i = 0; i < summaries.Length; i++)
            {
                DutySummary summary = summaries[i];
                if (summary != null && summary.gameObject.scene.IsValid() && summary.dutySummaryCanvas != null &&
                    summary.dutySummaryCanvas.enabled && summary.animator != null && summary.animator.gameObject.activeInHierarchy &&
                    (!String.IsNullOrWhiteSpace(ReadableTmpText(summary.detailsText)) ||
                     !String.IsNullOrWhiteSpace(ReadableTmpText(summary.statisticsText))))
                    return summary;
            }
            return null;
        }

        private void SpeakDutySummary()
        {
            DutySummary summary = FindActiveDutySummary();
            if (summary == null)
            {
                Speak("The duty results screen is not open.");
                return;
            }
            List<string> labels = TextLines(ReadableTmpText(summary.detailsText));
            List<string> values = TextLines(ReadableTmpText(summary.statisticsText));
            List<string> output = new List<string>();
            int count = Math.Max(labels.Count, values.Count);
            for (int i = 0; i < count; i++)
            {
                string label = i < labels.Count ? labels[i] : String.Empty;
                string value = i < values.Count ? values[i] : String.Empty;
                if (label.Length > 0 && value.Length > 0) output.Add(label + " " + value);
                else if (label.Length > 0) output.Add(label);
                else if (value.Length > 0) output.Add(value);
            }
            Speak(output.Count == 0 ? "Duty results are not available yet." : "Duty summary. " + String.Join(". ", output.ToArray()) + ".");
        }

        private void SpeakNextDutyResult(DutySummary summary, int direction)
        {
            List<IncidentInstance> results = new List<IncidentInstance>();
            // GameManager clears activeStatistics immediately after DutySummary.FillIncidentList.
            // The list rows retain the authoritative IncidentInstance references, but their
            // GameObjects can be inactive while the summary animator is displaying them.
            IncidentLabelNew[] labels = summary.dutySummaryCanvas.GetComponentsInChildren<IncidentLabelNew>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                IncidentInstance incident = labels[i] == null ? null : labels[i].Element;
                if (incident != null && !results.Contains(incident) &&
                    (incident.Scenes == null || incident.Scenes.Count == 0 ||
                     incident.Scenes[0].solveType != IncidentScene.SolveType.MERGED))
                    results.Add(incident);
            }
            if (results.Count == 0)
            {
                dutyResultIndex = -1;
                Speak("No incident results found.");
                return;
            }
            dutyResultIndex = dutyResultIndex < 0 ? (direction > 0 ? 0 : results.Count - 1) :
                (dutyResultIndex + direction + results.Count) % results.Count;
            IncidentInstance result = results[dutyResultIndex];
            string incidentId = result.Type == null ? String.Empty : result.Type.id;
            string name = String.IsNullOrWhiteSpace(incidentId) ? "Unknown incident" : LEManager.Get("incident." + incidentId + "." + incidentId);
            if (String.IsNullOrWhiteSpace(name)) name = SplitIdentifier(incidentId);
            List<string> outcomes = new List<string>();
            int maximumOpinion = 0;
            if (result.Scenes != null)
            {
                for (int i = 0; i < result.Scenes.Count; i++)
                {
                    IncidentScene scene = result.Scenes[i];
                    if (scene == null || scene.solveType == IncidentScene.SolveType.MERGED) continue;
                    maximumOpinion += scene.maximumOpinionEffect;
                    string outcome = DescribeSolveType(scene.solveType);
                    if (!outcomes.Contains(outcome)) outcomes.Add(outcome);
                }
            }
            string outcomeText = outcomes.Count == 0 ? "Outcome unavailable" : String.Join(", ", outcomes.ToArray());
            Speak("Incident result " + (dutyResultIndex + 1) + " of " + results.Count + ". " + name + ". " +
                outcomeText + ". Reputation " + FormatSignedNumber(result.opinionEffect) + " of " + maximumOpinion + ".");
        }

        private static string DescribeSolveType(IncidentScene.SolveType solveType)
        {
            switch (solveType)
            {
                case IncidentScene.SolveType.SOLVED: return "Solved";
                case IncidentScene.SolveType.TIMEOUT: return "Timed out";
                case IncidentScene.SolveType.IGNORED: return "Ignored";
                case IncidentScene.SolveType.NO_REACTION: return "No response";
                case IncidentScene.SolveType.BURNED: return "Unresolved";
                case IncidentScene.SolveType.IN_PROGRESS: return "Still in progress";
                default: return SplitIdentifier(solveType.ToString());
            }
        }

        private static string FormatSignedNumber(float value)
        {
            string number = Math.Abs(value % 1f) < 0.001f ? ((int)value).ToString() : value.ToString("0.##");
            return value > 0f ? "plus " + number : number;
        }

        private static void AppendVisibleTexts(StringBuilder result, Transform root)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TMP_Text[] tmpTexts = root.gameObject.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmpTexts.Length; i++)
                if (tmpTexts[i] != null && tmpTexts[i].gameObject.activeInHierarchy)
                    AppendText(result, ReadableTmpText(tmpTexts[i]), seen);
            Text[] legacyTexts = root.gameObject.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < legacyTexts.Length; i++)
                if (legacyTexts[i] != null && legacyTexts[i].gameObject.activeInHierarchy)
                    AppendText(result, legacyTexts[i].text, seen);
        }

        private static List<string> TextLines(string value)
        {
            List<string> lines = new List<string>();
            if (String.IsNullOrWhiteSpace(value)) return lines;
            string[] split = value.Replace("\r", String.Empty).Split('\n');
            for (int i = 0; i < split.Length; i++)
            {
                string line = split[i].Trim();
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }

        private static string GetIncidentName(Incident incident)
        {
            if (incident == null || incident.incidentInstance == null || incident.incidentInstance.Type == null)
                return "unknown incident";
            string incidentId = incident.incidentInstance.Type.id;
            string name = LEManager.Get("incident." + incidentId + "." + incidentId);
            return String.IsNullOrWhiteSpace(name) ? SplitIdentifier(incidentId) : name;
        }

        private static string IncidentLocationText(Incident incident)
        {
            if (incident == null || incident.scenes == null)
                return String.Empty;
            for (int i = 0; i < incident.scenes.Count; i++)
            {
                Scene scene = incident.scenes[i];
                if (scene != null && scene.IncidentScene != null && scene.IncidentScene.Location != null &&
                    !String.IsNullOrWhiteSpace(scene.IncidentScene.Location.address))
                    return " at " + scene.IncidentScene.Location.address;
            }
            return String.Empty;
        }

        private static string DescribeIncidentOperations(Incident incident)
        {
            if (incident == null || incident.scenes == null || incident.scenes.Count == 0)
                return "No active scene details are available.";
            bool police = false, medical = false, fire = false, fight = false;
            int policeTransport = 0, medicalTransport = 0;
            List<Unit> responding = new List<Unit>();
            List<Unit> onsite = new List<Unit>();
            for (int i = 0; i < incident.scenes.Count; i++)
            {
                Scene scene = incident.scenes[i];
                if (scene == null) continue;
                if (scene.unitsOnWay != null) responding.AddRange(scene.unitsOnWay.Where(u => u != null));
                if (scene.Onsite == null) continue;
                police |= OnsiteLogic.RequiresPolice(scene.Onsite);
                medical |= OnsiteLogic.RequiresMedical(scene.Onsite);
                fire |= OnsiteLogic.RequiresFire(scene.Onsite);
                fight |= scene.Onsite.onsiteFight;
                policeTransport += OnsiteLogic.GetSuspectsToTransportCount(scene.Onsite);
                medicalTransport += OnsiteLogic.GetInjuredToTransportCount(scene.Onsite);
                onsite.AddRange(scene.Onsite.units.Where(u => u != null));
            }
            responding = responding.Distinct().ToList();
            onsite = onsite.Distinct().ToList();
            List<string> details = new List<string>();
            List<string> required = new List<string>();
            if (police) required.Add("police");
            if (medical) required.Add("medical");
            if (fire) required.Add("fire");
            details.Add(required.Count == 0 ? "No unresolved service requirement is currently reported" : "Requires " + String.Join(", ", required.ToArray()));
            if (fight) details.Add("shots fired or an active fight reported");
            if (policeTransport > 0) details.Add(policeTransport + " police transport" + (policeTransport == 1 ? String.Empty : "s") + " needed");
            if (medicalTransport > 0) details.Add(medicalTransport + " medical transport" + (medicalTransport == 1 ? String.Empty : "s") + " needed");
            details.Add(responding.Count == 0 ? "no units responding" : "responding: " + String.Join(", ", responding.Select(UnitSpokenName).ToArray()));
            details.Add(onsite.Count == 0 ? "no units onsite" : "onsite: " + String.Join(", ", onsite.Select(UnitSpokenName).ToArray()));
            return String.Join(". ", details.ToArray()) + ".";
        }

        private static string UnitSpokenName(Unit unit)
        {
            if (unit == null || unit.Squad == null)
                return "unnamed unit";
            return unit.Squad.Name + " " + DescribeUnitType(unit.Squad.Type.ToString());
        }

        private void RefreshDistricts()
        {
            districts.Clear();
            DistrictDataTag[] found = Resources.FindObjectsOfTypeAll<DistrictDataTag>();
            for (int i = 0; i < found.Length; i++)
            {
                DistrictDataTag district = found[i];
                if (district == null || district.DistrictsData == null || !district.gameObject.scene.IsValid())
                    continue;
                districts.Add(district);
            }
            districts.Sort(delegate(DistrictDataTag a, DistrictDataTag b)
            {
                return String.Compare(a.GetNameText(), b.GetNameText(), StringComparison.OrdinalIgnoreCase);
            });
            if (districts.Count == 0)
                districtIndex = -1;
            else if (districtIndex >= districts.Count)
                districtIndex = 0;
        }

        private void SpeakNextDistrict(int direction)
        {
            RefreshDistricts();
            if (districts.Count == 0)
            {
                Speak("No selectable starting districts found.");
                return;
            }
            districtIndex = districtIndex < 0 ? (direction > 0 ? 0 : districts.Count - 1) :
                (districtIndex + direction + districts.Count) % districts.Count;
            DistrictDataTag district = districts[districtIndex];
            string state = district.TagState.ToString();
            string instruction;
            if (state == "Enabled") instruction = "Press F5 to select.";
            else if (state == "Chosen") instruction = "Currently selected. Press F5 to remove it if the game allows.";
            else instruction = "Unavailable on this screen. Choose an available district or expand through neighboring districts.";
            Speak("District " + (districtIndex + 1) + " of " + districts.Count + ". " + district.GetNameText() + ", " + DescribeDistrictState(state) + ". " + instruction);
        }

        private void CheckStartingAreaGuidance()
        {
            DistrictsViewPanel[] panels = Resources.FindObjectsOfTypeAll<DistrictsViewPanel>();
            bool isStartingAreaScreen = false;
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null && panels[i].gameObject.scene.IsValid() && panels[i].IsActive)
                {
                    isStartingAreaScreen = true;
                    break;
                }
            }
            if (isStartingAreaScreen && !wasStartingAreaScreen)
                Speak("Choose a starting district. Press F4 to browse districts, then F5 to select the district. After selecting, browse to Start and press Enter.");
            wasStartingAreaScreen = isStartingAreaScreen;
        }

        private void CheckDutySummaryGuidance()
        {
            bool visible = FindActiveDutySummary() != null;
            if (visible && !wasDutySummaryVisible)
            {
                dutyResultIndex = -1;
                Speak("Duty summary. Press F9 for overall statistics and finances. Press F3 to browse incident results, or Shift F3 to browse backward.");
            }
            if (!visible)
                dutyResultIndex = -1;
            wasDutySummaryVisible = visible;
        }

        private void ChooseCurrentDistrict()
        {
            RefreshDistricts();
            if (districtIndex < 0 || districtIndex >= districts.Count)
            {
                Speak("No starting district is selected for browsing. Press F4 first.");
                return;
            }
            DistrictsViewPanel activePanel = FindActiveDistrictPanel();
            if (activePanel == null)
            {
                Speak("The starting-area screen is not active.");
                return;
            }
            DistrictDataTag district = districts[districtIndex];
            if (district.TagState.ToString() == "Disabled")
            {
                Speak(district.GetNameText() + " is unavailable. Select an available district or expand through an adjacent district first.");
                return;
            }
            activePanel.DistrictClicked(district);
            string points = activePanel.AvailableCareerPoints >= 0 ? " " + activePanel.AvailableCareerPoints + " Career Points remaining." : String.Empty;
            Speak(district.GetNameText() + ", " + DescribeDistrictState(district.TagState.ToString()) + "." + points + " Browse to Start and press Enter.");
        }

        private static DistrictsViewPanel FindActiveDistrictPanel()
        {
            DistrictsViewPanel[] panels = Resources.FindObjectsOfTypeAll<DistrictsViewPanel>();
            for (int i = 0; i < panels.Length; i++)
                if (panels[i] != null && panels[i].gameObject.scene.IsValid() && panels[i].IsActive)
                    return panels[i];
            return null;
        }

        private void OpenNewIncident()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.InGameUI == null || manager.InGameUI.newIncidentsButtonsPanel == null)
            {
                Speak("The duty incident controls are not available.");
                return;
            }
            Button button = manager.InGameUI.newIncidentsButtonsPanel.newIncidentButton;
            if (button == null || !button.gameObject.activeInHierarchy)
            {
                Speak("There is no new incident alert.");
                return;
            }
            button.onClick.Invoke();
            Speak("Opened the new incident.");
        }

        private void AnswerNewCall()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.InGameUI == null || manager.InGameUI.newIncidentsButtonsPanel == null)
            {
                Speak("The duty call controls are not available.");
                return;
            }
            if (!manager.InGameUI.newIncidentsButtonsPanel.IsConversationButtonActive)
            {
                Speak("There is no new call waiting.");
                return;
            }
            manager.InGameUI.newIncidentsButtonsPanel.StartNewConversationButton();
            Speak("Opened the new call.");
        }

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.GetSiblingIndex().ToString("D4") + ":" + transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.GetSiblingIndex().ToString("D4") + ":" + transform.name + "/" + path;
            }
            return path;
        }

        private void Speak(string text)
        {
            if (String.IsNullOrEmpty(text))
                return;
            Logger.LogInfo("SPEAK: " + text);
            try
            {
                if (nvdaLoaded && nvdaController_testIfRunning() == 0)
                {
                    nvdaController_cancelSpeech();
                    nvdaController_speakText(text);
                    nvdaController_brailleMessage(text);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("NVDA speech failed: " + ex.Message);
            }
        }

        private void DispatchSelected()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null)
            {
                Speak("Game manager not available.");
                return;
            }
            if (selectedUnit == null || manager.unitList == null || !manager.unitList.Contains(selectedUnit))
            {
                Speak("No unit is selected. Use F2 to select a unit.");
                return;
            }
            if (incidentIndex < 0 || manager.incidentList == null || incidentIndex >= manager.incidentList.Count)
            {
                Speak("No incident is selected. Use F3 to select an incident.");
                return;
            }

            Unit unit = selectedUnit;
            Incident incident = manager.incidentList[incidentIndex];

            if (unit == null)
            {
                Speak("Selected unit is invalid.");
                return;
            }
            if (incident == null || incident.scenes == null || incident.scenes.Count == 0)
            {
                Speak("Selected incident has no active scenes.");
                return;
            }

            Scene scene = incident.scenes[0];
            unit.EnqueueSolveIncidentTask(scene, true, true);

            string unitName = unit.Squad == null ? "the unit" : unit.Squad.Name;
            string incidentName = "the incident";
            if (incident.incidentInstance != null && incident.incidentInstance.Type != null)
            {
                string incidentId = incident.incidentInstance.Type.id;
                // Use the game's own localized display name, matching the F3
                // browsing announcement (key incident.{id}.{id}).
                incidentName = LEManager.Get("incident." + incidentId + "." + incidentId);
                if (String.IsNullOrWhiteSpace(incidentName))
                    incidentName = incidentId;
            }
            Speak("Dispatched " + unitName + " to " + incidentName + ".");
        }

        private void CheckNewAlerts()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.InGameUI == null || manager.InGameUI.newIncidentsButtonsPanel == null)
            {
                wasNewIncidentAlert = false;
                wasNewCallAlert = false;
                return;
            }

            Button incidentButton = manager.InGameUI.newIncidentsButtonsPanel.newIncidentButton;
            bool isNewIncident = incidentButton != null && incidentButton.gameObject.activeInHierarchy;
            if (isNewIncident && !wasNewIncidentAlert)
                Speak("New incident reported. Press F6 to open.");
            wasNewIncidentAlert = isNewIncident;

            bool isNewCall = manager.InGameUI.newIncidentsButtonsPanel.IsConversationButtonActive;
            if (isNewCall && !wasNewCallAlert)
                Speak("New call waiting. Press F7 to answer.");
            wasNewCallAlert = isNewCall;
        }

        private void CheckUnitStates()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.unitList == null) return;

            foreach (Unit unit in manager.unitList)
            {
                if (unit == null || unit.Squad == null) continue;
                int id = unit.GetInstanceID();
                
                string stateKey = unit.CurrentState == null ? "none" : unit.CurrentState.UnitStatus.ToString();
                string taskKey = unit.CurrentTask == null ? "none" : unit.CurrentTask.GetType().FullName;
                string currentStateTask = stateKey + "|" + taskKey;
                
                if (!unitTasks.ContainsKey(id))
                {
                    unitTasks[id] = currentStateTask;
                }
                else if (unitTasks[id] != currentStateTask)
                {
                    unitTasks[id] = currentStateTask;
                    
                    // Filter out continuous path updates that spam
                    string taskName = unit.CurrentTask == null ? String.Empty : unit.CurrentTask.GetType().Name;
                    if (taskName != "UpdatePathSeekTask" && taskName != "UpdatePathTask")
                    {
                        string activity = DescribeUnitActivity(unit);
                        string announcementKey = id + "|" + activity;
                        DateTime lastAnnouncement;
                        DateTime now = DateTime.UtcNow;
                        if (!recentUnitAnnouncements.TryGetValue(announcementKey, out lastAnnouncement) ||
                            (now - lastAnnouncement).TotalSeconds >= 30)
                        {
                            recentUnitAnnouncements[announcementKey] = now;
                            Speak(unit.Squad.Name + " is now " + activity + ".");
                        }
                    }
                }
            }
        }

        private static string DescribeUnitActivity(Unit unit)
        {
            string status = "idle";
            if (unit != null && unit.CurrentState != null)
                status = DescribeUnitStatus(unit.CurrentState.UnitStatus);

            string task = DescribeUnitTask(unit == null || unit.CurrentTask == null ? null : unit.CurrentTask.GetType().Name);
            if (String.IsNullOrEmpty(task) || task == status)
                return status;
            if (status == "idle" && task == "patrolling")
                return task;
            return status + ", " + task;
        }

        private static string DescribeUnitStatus(UnitStatusType status)
        {
            switch (status)
            {
                case UnitStatusType.IDLE: return "idle";
                case UnitStatusType.CHASING: return "chasing";
                case UnitStatusType.GOING: return "travelling";
                case UnitStatusType.GOING_TO_INCIDENT: return "responding to an incident";
                case UnitStatusType.GOING_TO_BASE: return "returning to base";
                case UnitStatusType.SOLVING_INCIDENT: return "working at an incident";
                case UnitStatusType.INACTIVE: return "inactive";
                case UnitStatusType.IMMOBILIZED: return "immobilized";
                case UnitStatusType.FIGHTING: return "fighting";
                case UnitStatusType.PATROLLING: return "patrolling";
                case UnitStatusType.DEAD: return "out of service";
                case UnitStatusType.DEPLOYED: return "deployed";
                default: return SplitIdentifier(status.ToString()).ToLowerInvariant();
            }
        }

        private static string DescribeUnitTask(string taskName)
        {
            if (String.IsNullOrEmpty(taskName)) return null;
            switch (taskName)
            {
                case "PatrolTask": return "patrolling";
                case "RoadMoveTask": return "travelling by road";
                case "SolveSceneTask": return "assigned to an incident";
                case "TransportToHospitalTask": return "transporting to hospital";
                case "TransportToPoliceStationTask": return "transporting to a police station";
                case "TransportToFireStationTask": return "returning to a fire station";
                case "TransportToPOITask": return "travelling to a facility";
                case "GoToPOITask": return "travelling to a facility";
                case "GoToTheSceneAndWaitTask": return "responding and waiting nearby";
                case "CityFollowTask": return "pursuing a moving incident";
                case "RefillWaterTask": return "refilling water";
                case "EscapeFromBigFireTask": return "escaping a large fire";
                default:
                    if (taskName.EndsWith("Task", StringComparison.Ordinal))
                        taskName = taskName.Substring(0, taskName.Length - 4);
                    return SplitIdentifier(taskName).ToLowerInvariant();
            }
        }

        private static string DescribeUnitType(string value)
        {
            switch (value)
            {
                case "POLICE": return "police";
                case "MEDICAL": return "medical";
                case "FIRE": return "fire";
                case "ARMY": return "army";
                default: return SplitIdentifier(value).ToLowerInvariant();
            }
        }

        private static string DescribeDistrictState(string value)
        {
            switch (value)
            {
                case "Chosen": return "selected";
                case "Enabled": return "available";
                case "Disabled": return "unavailable";
                default: return SplitIdentifier(value).ToLowerInvariant();
            }
        }

        private static string SplitIdentifier(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return value;
            StringBuilder result = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '_' || c == '-')
                {
                    if (result.Length > 0 && result[result.Length - 1] != ' ')
                        result.Append(' ');
                    continue;
                }
                if (i > 0 && Char.IsUpper(c) && Char.IsLower(value[i - 1]) && result[result.Length - 1] != ' ')
                    result.Append(' ');
                result.Append(c);
            }
            return result.ToString().Trim();
        }

        private void CheckIncidentChanges()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.incidentList == null) return;

            // On first poll, just snapshot the current state so we don't
            // announce every existing incident as "new".
            if (!incidentTrackingInitialised)
            {
                knownIncidentIds.Clear();
                incidentSceneCounts.Clear();
                for (int i = 0; i < manager.incidentList.Count; i++)
                {
                    Incident inc = manager.incidentList[i];
                    if (inc == null) continue;
                    int id = inc.GetInstanceID();
                    knownIncidentIds.Add(id);
                    incidentSceneCounts[id] = inc.scenes == null ? 0 : inc.scenes.Count;
                }
                incidentTrackingInitialised = true;
                return;
            }

            // Build the current snapshot.
            HashSet<int> currentIds = new HashSet<int>();
            Dictionary<int, int> currentScenes = new Dictionary<int, int>();
            for (int i = 0; i < manager.incidentList.Count; i++)
            {
                Incident inc = manager.incidentList[i];
                if (inc == null) continue;
                int id = inc.GetInstanceID();
                currentIds.Add(id);
                currentScenes[id] = inc.scenes == null ? 0 : inc.scenes.Count;
            }

            // Detect resolved incidents (were known, now gone).
            foreach (int oldId in knownIncidentIds)
            {
                if (!currentIds.Contains(oldId))
                {
                    Speak("An incident has been resolved.");
                    incidentSceneCounts.Remove(oldId);
                }
            }

            // Detect new incidents and scene changes.
            foreach (int id in currentIds)
            {
                if (!knownIncidentIds.Contains(id))
                {
                    // New incident appeared — find it to get its name.
                    for (int i = 0; i < manager.incidentList.Count; i++)
                    {
                        Incident inc = manager.incidentList[i];
                        if (inc == null || inc.GetInstanceID() != id) continue;
                        string name = "an incident";
                        if (inc.incidentInstance != null && inc.incidentInstance.Type != null)
                        {
                            string incidentId = inc.incidentInstance.Type.id;
                            string loc = LEManager.Get("incident." + incidentId + "." + incidentId);
                            if (!String.IsNullOrWhiteSpace(loc)) name = loc;
                        }
                        int sc = inc.scenes == null ? 0 : inc.scenes.Count;
                        Speak("New incident: " + name + ", " + sc + " scene" + (sc == 1 ? String.Empty : "s") + ".");
                        break;
                    }
                }
                else
                {
                    // Existing incident — check scene count change.
                    int oldCount;
                    incidentSceneCounts.TryGetValue(id, out oldCount);
                    int newCount = currentScenes[id];
                    if (newCount > oldCount)
                    {
                        Speak("Scene added to an incident. " + newCount + " active scene" + (newCount == 1 ? String.Empty : "s") + ".");
                    }
                    else if (newCount < oldCount && newCount > 0)
                    {
                        Speak("Scene resolved. " + newCount + " active scene" + (newCount == 1 ? String.Empty : "s") + " remaining.");
                    }
                    else if (newCount == 0 && oldCount > 0)
                    {
                        Speak("All scenes in an incident have been resolved.");
                    }
                }
            }

            knownIncidentIds.Clear();
            foreach (int id in currentIds)
                knownIncidentIds.Add(id);
            incidentSceneCounts.Clear();
            foreach (var kv in currentScenes)
                incidentSceneCounts[kv.Key] = kv.Value;
        }

        private void CheckIncidentOperationalChanges()
        {
            GameManager manager = UnityEngine.Object.FindObjectOfType<GameManager>();
            if (manager == null || manager.incidentList == null)
            {
                sceneOperationalStates.Clear();
                return;
            }
            HashSet<int> currentSceneIds = new HashSet<int>();
            for (int i = 0; i < manager.incidentList.Count; i++)
            {
                Incident incident = manager.incidentList[i];
                if (incident == null || incident.scenes == null) continue;
                for (int j = 0; j < incident.scenes.Count; j++)
                {
                    Scene scene = incident.scenes[j];
                    if (scene == null || scene.Onsite == null) continue;
                    int id = scene.GetInstanceID();
                    currentSceneIds.Add(id);
                    IncidentOperationalState current = GetOperationalState(scene);
                    IncidentOperationalState previous;
                    if (sceneOperationalStates.TryGetValue(id, out previous))
                        AnnounceOperationalChanges(incident, previous, current);
                    sceneOperationalStates[id] = current;
                }
            }
            List<int> removed = sceneOperationalStates.Keys.Where(id => !currentSceneIds.Contains(id)).ToList();
            for (int i = 0; i < removed.Count; i++)
                sceneOperationalStates.Remove(removed[i]);
        }

        private static IncidentOperationalState GetOperationalState(Scene scene)
        {
            IncidentOperationalState state = new IncidentOperationalState();
            if (scene == null || scene.Onsite == null)
                return state;
            scene.Onsite.PoliceFireMedicNeed(out state.PoliceNeeded, out state.MedicalNeeded, out state.FireNeeded);
            state.Fight = scene.Onsite.onsiteFight;
            state.PoliceTransports = OnsiteLogic.GetSuspectsToTransportCount(scene.Onsite);
            state.MedicalTransports = OnsiteLogic.GetInjuredToTransportCount(scene.Onsite);
            return state;
        }

        private void AnnounceOperationalChanges(Incident incident, IncidentOperationalState previous, IncidentOperationalState current)
        {
            List<string> changes = new List<string>();
            if (current.Fight && !previous.Fight) changes.Add("shots fired or an active fight reported");
            if (current.PoliceNeeded && !previous.PoliceNeeded) changes.Add("police unit needed");
            if (current.MedicalNeeded && !previous.MedicalNeeded) changes.Add("medical unit needed");
            if (current.FireNeeded && !previous.FireNeeded) changes.Add("fire unit needed");
            if (current.PoliceTransports > previous.PoliceTransports)
                changes.Add((current.PoliceTransports - previous.PoliceTransports) + " new police transport needed");
            if (current.MedicalTransports > previous.MedicalTransports)
                changes.Add((current.MedicalTransports - previous.MedicalTransports) + " new medical transport needed");
            if (changes.Count == 0)
                return;
            Speak(GetIncidentName(incident) + IncidentLocationText(incident) + ": " + String.Join("; ", changes.ToArray()) + ".");
        }
    }
}
