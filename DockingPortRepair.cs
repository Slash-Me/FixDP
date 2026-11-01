using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DockingPortRepair
{
    // =====================================================================
    //  Кнопка в меню детали (правый клик по порту)
    // =====================================================================

    /// <summary>
    /// Кнопка «Принудительная расстыковка» на стыковочном порту.
    /// Работает по структуре соединения деталей, а не по состояниям портов.
    /// </summary>
    public class ModuleDockingPortRepair : PartModule
    {
        /// <summary>true = кнопка только при рассинхроне состояний; false (по умолчанию) = у любой стыкованной пары.</summary>
        [KSPField] public bool onlyWhenDesynced = false;

        /// <summary>Скорость разлёта после расстыковки, м/с.</summary>
        [KSPField] public float separationSpeed = RepairCore.DefaultSeparationSpeed;

        /// <summary>Насколько (в метрах) аппараты должны разойтись, прежде чем порты снова смогут стыковаться.</summary>
        [KSPField] public float reEngageDistance = RepairCore.DefaultReEngageDistance;

        /// <summary>Показывать в меню детали строку диагностики «Починка порта».</summary>
        [KSPField] public bool debugStatus = true;

        [KSPField(guiActive = true, guiActiveUnfocused = true, unfocusedRange = 3f, guiName = "Починка порта")]
        public string status = "";

        private const float CheckInterval = 0.5f;
        private const float ConfirmWindow = 5f;

        private float nextCheck;
        private float armedUntil;
        private bool errorLogged;
        private ModuleDockingNode node;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            node = part.FindModuleImplementing<ModuleDockingNode>();
            if (node == null)
            {
                enabled = false;
                return;
            }

            if (!DockingAccess.Available)
                Debug.LogWarning(RepairCore.Tag + "не удалось получить доступ к полям ModuleDockingNode (state/fsm). Мод работать не будет.");

            Debug.Log(RepairCore.Tag + "модуль активен на детали " + part.partInfo.name);
            SetEventActive(false);
        }

        public override void OnUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (node == null) node = part.FindModuleImplementing<ModuleDockingNode>();
            if (node == null) return;

            if (Time.time < nextCheck) return;
            nextCheck = Time.time + CheckInterval;

            bool show = false;
            string info = "";
            try
            {
                ModuleDockingNode other;
                show = RepairCore.ShouldOffer(node, onlyWhenDesynced, out other, out info);
            }
            catch (Exception e)
            {
                LogErrorOnce(e);
                info = "ошибка, см. KSP.log";
            }

            status = info;
            BaseField f = Fields["status"];
            if (f != null)
            {
                f.guiActive = debugStatus;
                f.guiActiveUnfocused = debugStatus;
            }
            SetEventActive(show);
        }

        private void SetEventActive(bool value)
        {
            BaseEvent ev = Events["ForceUndock"];
            if (ev != null) ev.active = value;
        }

        [KSPEvent(guiActive = true, guiActiveUnfocused = true, unfocusedRange = 3f,
                  guiName = "Принудительная расстыковка", active = false)]
        public void ForceUndock()
        {
            try
            {
                // Защита от случайного нажатия: нужно нажать дважды в течение нескольких секунд.
                if (Time.time > armedUntil)
                {
                    armedUntil = Time.time + ConfirmWindow;
                    RepairCore.Msg("Нажмите «Принудительная расстыковка» ещё раз в течение 5 с для подтверждения.");
                    return;
                }
                armedUntil = 0f;

                ModuleDockingNode other = RepairCore.FindPartner(node);
                if (other == null)
                {
                    RepairCore.Msg("Парный стыковочный порт не найден.");
                    return;
                }

                string error = RepairCore.ForceUndock(node, other, separationSpeed, reEngageDistance);
                RepairCore.Msg(error ?? "Порты расстыкованы. Отлетите на пару метров, и они снова смогут стыковаться.");
            }
            catch (Exception e)
            {
                LogErrorOnce(e);
                RepairCore.Msg("Ошибка принудительной расстыковки. Подробности в KSP.log.");
            }
        }

        private void LogErrorOnce(Exception e)
        {
            if (errorLogged) return;
            errorLogged = true;
            Debug.LogError(RepairCore.Tag + e);
        }
    }

    // =====================================================================
    //  Общая логика
    // =====================================================================

    internal static class RepairCore
    {
        public const string Tag = "[DockingPortRepair] ";
        public const float DefaultSeparationSpeed = 0.3f;
        public const float DefaultReEngageDistance = 3f;
        private const float MaxWaitSeconds = 300f;

        /// <summary>Расстыкованные принудительно пары, которые ещё нельзя возвращать в Ready.</summary>
        private sealed class PendingRearm
        {
            public ModuleDockingNode A, B;
            public Part Child, Keep;
            public float Speed, Need, StartTime, D0;
            public int Frames;
            public bool Pushed;
            public float[] SavedA, SavedB;
        }

        private static readonly List<PendingRearm> Pending = new List<PendingRearm>();

        public static void Msg(string text)
        {
            ScreenMessages.PostScreenMessage(text, 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        public static void ClearPending()
        {
            Pending.Clear();
        }

        /// <summary>Ищет стыковочный порт, соединённый с этим портом (родитель или потомок в дереве деталей).</summary>
        public static ModuleDockingNode FindPartner(ModuleDockingNode node)
        {
            Part part = node.part;
            List<ModuleDockingNode> candidates = new List<ModuleDockingNode>();

            if (part.parent != null)
                candidates.AddRange(part.parent.FindModulesImplementing<ModuleDockingNode>());

            for (int i = 0; i < part.children.Count; i++)
                candidates.AddRange(part.children[i].FindModulesImplementing<ModuleDockingNode>());

            if (candidates.Count == 0) return null;

            uint myUid = DockingAccess.GetDockUId(node);
            ModuleDockingNode best = null;
            int bestScore = -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                ModuleDockingNode c = candidates[i];
                if (c == node) continue;

                int score = 0;
                if (myUid != 0 && c.part.flightID == myUid) score += 4;
                if (part.flightID != 0 && DockingAccess.GetDockUId(c) == part.flightID) score += 4;
                if (DockingAccess.IsDocked(DockingAccess.GetState(c))) score += 2;

                if (score > bestScore)
                {
                    best = c;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>Нужно ли предлагать принудительную расстыковку для этого порта.</summary>
        public static bool ShouldOffer(ModuleDockingNode node, bool onlyWhenDesynced, out ModuleDockingNode other, out string info)
        {
            info = "";
            other = null;

            if (!DockingAccess.Available)
            {
                info = "нет доступа к полям порта";
                return false;
            }

            string a = DockingAccess.GetState(node);
            other = FindPartner(node);
            if (other == null)
            {
                info = "парный порт не найден (порт: " + a + ")";
                return false;
            }

            string b = DockingAccess.GetState(other);
            info = a + " | " + b;

            // Стыковка «сам с собой» — у игры для неё свои кнопки.
            if (DockingAccess.IsSameVessel(a) || DockingAccess.IsSameVessel(b)) return false;

            // Пара, собранная в редакторе (PreAttached), — это не стыковка, а штатное соединение.
            if (DockingAccess.IsPreAttached(a) && DockingAccess.IsPreAttached(b)) return false;

            if (onlyWhenDesynced && DockingAccess.IsSyncedPair(a, b)) return false;

            return true;
        }

        /// <summary>
        /// Расстыковывает пару портов. Возвращает null при успехе или текст ошибки.
        /// Дальше за парой следит ProcessPending(): порты возвращаются в Ready, только когда аппараты разошлись.
        /// </summary>
        public static string ForceUndock(ModuleDockingNode a, ModuleDockingNode b, float speed, float reEngageDistance)
        {
            for (int i = 0; i < Pending.Count; i++)
                if ((Pending[i].A == a && Pending[i].B == b) || (Pending[i].A == b && Pending[i].B == a))
                    return "Эта пара уже расстыкована, подождите.";

            Part child;
            Part keep;
            if (a.part.parent == b.part)
            {
                child = a.part;
                keep = b.part;
            }
            else if (b.part.parent == a.part)
            {
                child = b.part;
                keep = a.part;
            }
            else
            {
                return "Порты не соединены напрямую, расстыковка отменена.";
            }

            Debug.Log(Tag + "принудительная расстыковка: " + keep.partInfo.name + " <-> " + child.partInfo.name);

            PendingRearm p = new PendingRearm();
            p.A = a;
            p.B = b;
            p.Child = child;
            p.Keep = keep;
            p.Speed = speed;
            p.Need = Mathf.Max(reEngageDistance, 0.5f);
            p.StartTime = Time.time;

            // 1) Сначала запрещаем порту «хватать» соседа, чтобы он не пристыковался обратно.
            p.SavedA = DockingAccess.DisableCapture(a);
            p.SavedB = DockingAccess.DisableCapture(b);

            // 2) Физически разрываем соединение.
            child.decouple();

            // 3) Штатное состояние «только что расстыковались». Из него порт сам вернётся в Ready,
            //    а мы вернём его принудительно, когда аппараты разойдутся. В Ready раньше времени не ставим.
            DockingAccess.ClearUids(a);
            DockingAccess.ClearUids(b);
            DockingAccess.SetOtherNode(a, b);
            DockingAccess.SetOtherNode(b, a);
            if (!DockingAccess.SetState(a, "Disengage") || !DockingAccess.SetState(b, "Disengage"))
                Debug.LogWarning(Tag + "не удалось перевести порты в Disengage; защита от повторной стыковки держится только на обнулении дальности захвата.");

            Pending.Add(p);
            return null;
        }

        /// <summary>Вызывать каждый FixedUpdate: толкает аппараты друг от друга и возвращает порты в Ready, когда они разошлись.</summary>
        public static void ProcessPending()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingRearm p = Pending[i];
                bool done = false;

                if (p.A == null || p.B == null || p.Child == null || p.Keep == null)
                {
                    done = true;
                }
                else
                {
                    p.Frames++;
                    float dist = Vector3.Distance(p.Child.transform.position, p.Keep.transform.position);

                    if (!p.Pushed)
                    {
                        if (p.Frames >= 2)
                        {
                            // Физика нового аппарата создаётся не мгновенно — пробуем каждый кадр, пока не получится.
                            if (TryPush(p.Child, p.Keep, p.Speed))
                            {
                                p.Pushed = true;
                                p.D0 = dist;
                            }
                            else if (p.Frames > 150)
                            {
                                p.Pushed = true;
                                p.D0 = dist;
                                Debug.LogWarning(Tag + "не удалось оттолкнуть аппараты друг от друга (нет отдельных аппаратов или физики). Отлетите вручную.");
                            }
                        }
                    }
                    else if (dist > p.D0 + p.Need)
                    {
                        done = true;
                    }

                    // Аппараты ушли на рельсы (перемотка времени) — стыковаться всё равно нельзя, как и в стоке.
                    if ((p.Child.vessel != null && p.Child.vessel.packed) || (p.Keep.vessel != null && p.Keep.vessel.packed))
                        done = true;

                    if (Time.time - p.StartTime > MaxWaitSeconds)
                        done = true;
                }

                if (done)
                {
                    Finish(p);
                    Pending.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Расталкивает два аппарата с относительной скоростью speed (м/с), сохраняя импульс:
        /// лёгкий аппарат улетает быстрее тяжёлого. Скорость добавляется ВСЕМ деталям аппарата,
        /// а не одной, иначе соединения деталей просто «съедят» толчок.
        /// </summary>
        private static bool TryPush(Part child, Part keep, float speed)
        {
            if (speed <= 0f) return true;

            Vessel vc = child.vessel;
            Vessel vk = keep.vessel;
            if (vc == null || vk == null || vc == vk) return false;

            float mc = VesselMass(vc);
            float mk = VesselMass(vk);
            if (mc <= 0f || mk <= 0f) return false;

            Vector3 dir = (child.transform.position - keep.transform.position).normalized;
            float total = mc + mk;

            AddVelocity(vc, dir * (speed * mk / total));
            AddVelocity(vk, -dir * (speed * mc / total));

            Debug.Log(Tag + "толчок при расстыковке: " + speed.ToString("F2") + " м/с, массы " + mc.ToString("F2") + " и " + mk.ToString("F2") + " т");
            return true;
        }

        private static float VesselMass(Vessel v)
        {
            float mass = 0f;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Rigidbody rb = v.parts[i].Rigidbody;
                if (rb != null) mass += rb.mass;
            }
            return mass;
        }

        private static void AddVelocity(Vessel v, Vector3 dv)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Rigidbody rb = v.parts[i].Rigidbody;
                if (rb != null) rb.AddForce(dv, ForceMode.VelocityChange);
            }
        }

        private static void Finish(PendingRearm p)
        {
            Rearm(p.A, p.SavedA);
            Rearm(p.B, p.SavedB);
            Msg("Порты снова готовы к стыковке.");
        }

        private static void Rearm(ModuleDockingNode n, float[] savedCapture)
        {
            if (n == null) return;

            DockingAccess.RestoreCapture(n, savedCapture);

            if (DockingAccess.IsDocked(DockingAccess.GetState(n))) return;   // за это время успели пристыковаться

            DockingAccess.ClearUids(n);
            DockingAccess.SetOtherNode(n, null);
            DockingAccess.SetState(n, "Ready");
        }
    }

    // =====================================================================
    //  Аддон: окно со списком пар, страховочное добавление модуля, слежение за расстыкованными парами
    // =====================================================================

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DockingPortRepairAddon : MonoBehaviour
    {
        private sealed class PairInfo
        {
            public ModuleDockingNode A, B;
            public string Key, TitleA, TitleB, StateA, StateB;
            public bool CanForce;
        }

        private const int WindowId = 94827163;

        private float nextInject;
        private float nextPairScan;
        private bool showWindow;
        private Rect windowRect = new Rect(240f, 140f, 520f, 380f);
        private Vector2 scroll;
        private readonly List<PairInfo> pairs = new List<PairInfo>();

        private string armedKey = "";
        private float armedUntil;
        private PairInfo requested;

        private void Awake()
        {
            RepairCore.ClearPending();
        }

        private void Update()
        {
            // Горячая клавиша: Правый Ctrl + U — окно со списком пар портов.
            if (Input.GetKey(KeyCode.RightControl) && Input.GetKeyDown(KeyCode.U))
                showWindow = !showWindow;

            if (Time.time >= nextInject)
            {
                nextInject = Time.time + 2f;
                InjectModules();
            }

            if (showWindow && Time.time >= nextPairScan)
            {
                nextPairScan = Time.time + 0.5f;
                RebuildPairs();
            }

            if (requested != null)
            {
                PairInfo r = requested;
                requested = null;
                try
                {
                    string error = RepairCore.ForceUndock(r.A, r.B, RepairCore.DefaultSeparationSpeed, RepairCore.DefaultReEngageDistance);
                    RepairCore.Msg(error ?? "Порты расстыкованы. Отлетите на пару метров, и они снова смогут стыковаться.");
                }
                catch (Exception e)
                {
                    Debug.LogError(RepairCore.Tag + e);
                    RepairCore.Msg("Ошибка принудительной расстыковки. Подробности в KSP.log.");
                }
                nextPairScan = 0f;
            }
        }

        private void FixedUpdate()
        {
            try
            {
                RepairCore.ProcessPending();
            }
            catch (Exception e)
            {
                Debug.LogError(RepairCore.Tag + e);
            }
        }

        /// <summary>Если на стыковочном порту загруженного аппарата модуля-починки нет, добавляем его.</summary>
        private void InjectModules()
        {
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return;

            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v == null || !v.loaded) continue;

                for (int j = 0; j < v.parts.Count; j++)
                {
                    Part p = v.parts[j];
                    if (p == null) continue;
                    if (p.FindModuleImplementing<ModuleDockingNode>() == null) continue;
                    if (p.FindModuleImplementing<ModuleDockingPortRepair>() != null) continue;

                    try
                    {
                        p.AddModule("ModuleDockingPortRepair", true);
                        Debug.Log(RepairCore.Tag + "модуль добавлен вручную на " + p.partInfo.name + " (" + v.vesselName + ")");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(RepairCore.Tag + "не удалось добавить модуль на " + p.partInfo.name + ": " + e);
                    }
                }
            }
        }

        private void RebuildPairs()
        {
            pairs.Clear();
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            HashSet<string> seen = new HashSet<string>();

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                ModuleDockingNode n = p.FindModuleImplementing<ModuleDockingNode>();
                if (n == null) continue;

                ModuleDockingNode o;
                string info;
                bool can = RepairCore.ShouldOffer(n, false, out o, out info);
                if (o == null) continue;

                uint k1 = Math.Min(p.flightID, o.part.flightID);
                uint k2 = Math.Max(p.flightID, o.part.flightID);
                string key = k1 + "_" + k2;
                if (!seen.Add(key)) continue;

                PairInfo pi = new PairInfo();
                pi.A = n;
                pi.B = o;
                pi.Key = key;
                pi.TitleA = p.partInfo.title;
                pi.TitleB = o.part.partInfo.title;
                pi.StateA = DockingAccess.GetState(n);
                pi.StateB = DockingAccess.GetState(o);
                pi.CanForce = can;
                pairs.Add(pi);
            }
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            GUI.skin = HighLogic.Skin;
            windowRect = GUILayout.Window(WindowId, windowRect, DrawWindow, "Починка стыковочных портов");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Пары соединённых стыковочных портов на активном аппарате:");

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(280f));

            if (pairs.Count == 0)
                GUILayout.Label("Пар не найдено.");

            for (int i = 0; i < pairs.Count; i++)
            {
                PairInfo pi = pairs[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(pi.TitleA + "  [" + pi.StateA + "]");
                GUILayout.Label(pi.TitleB + "  [" + pi.StateB + "]");

                if (pi.CanForce)
                {
                    bool armed = armedKey == pi.Key && Time.time < armedUntil;
                    string label = armed ? "Нажмите ещё раз для подтверждения" : "Принудительная расстыковка";
                    if (GUILayout.Button(label))
                    {
                        if (armed)
                        {
                            armedKey = "";
                            requested = pi;      // выполним в Update, чтобы не менять аппарат посреди отрисовки окна
                        }
                        else
                        {
                            armedKey = pi.Key;
                            armedUntil = Time.time + 5f;
                        }
                    }
                }
                else
                {
                    GUILayout.Label("(собрано в редакторе или стыковка «сам с собой» — не трогаю)");
                }
                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("Закрыть"))
                showWindow = false;

            GUI.DragWindow();
        }
    }

    // =====================================================================
    //  Доступ к внутренностям ModuleDockingNode
    // =====================================================================

    /// <summary>
    /// Рефлексия вместо прямых обращений к полям ModuleDockingNode, чтобы плагин не зависел
    /// от точных имён членов класса и не падал, если разработчики их изменят.
    /// </summary>
    internal static class DockingAccess
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo StateField = typeof(ModuleDockingNode).GetField("state", Flags);
        private static readonly FieldInfo UidField = typeof(ModuleDockingNode).GetField("dockUId", Flags);
        private static readonly FieldInfo DockedPartUidField = typeof(ModuleDockingNode).GetField("dockedPartUId", Flags);
        private static readonly FieldInfo OtherNodeField = typeof(ModuleDockingNode).GetField("otherNode", Flags);
        private static readonly FieldInfo FsmField = typeof(ModuleDockingNode).GetField("fsm", Flags);

        private static readonly string[] CaptureFieldNames = { "acquireRange", "captureRange" };
        private static readonly FieldInfo[] CaptureFields = BuildCaptureFields();

        private static FieldInfo[] BuildCaptureFields()
        {
            List<FieldInfo> list = new List<FieldInfo>();
            for (int i = 0; i < CaptureFieldNames.Length; i++)
            {
                FieldInfo f = typeof(ModuleDockingNode).GetField(CaptureFieldNames[i], Flags);
                if (f != null && f.FieldType == typeof(float)) list.Add(f);
            }
            return list.ToArray();
        }

        public static bool Available
        {
            get { return StateField != null || FsmField != null; }
        }

        /// <summary>Текущее состояние порта. Сначала спрашиваем у конечного автомата, потом у сохраняемого поля.</summary>
        public static string GetState(ModuleDockingNode n)
        {
            object fsm = FsmField != null ? FsmField.GetValue(n) : null;
            if (fsm != null)
            {
                Type t = fsm.GetType();

                PropertyInfo p = t.GetProperty("currentStateName", Flags);
                if (p != null)
                {
                    string s = p.GetValue(fsm, null) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                PropertyInfo cp = t.GetProperty("CurrentState", Flags);
                if (cp != null)
                {
                    object st = cp.GetValue(fsm, null);
                    if (st != null)
                    {
                        FieldInfo nf = st.GetType().GetField("name", Flags);
                        if (nf != null)
                        {
                            string s2 = nf.GetValue(st) as string;
                            if (!string.IsNullOrEmpty(s2)) return s2;
                        }
                    }
                }
            }
            return StateField != null ? StateField.GetValue(n) as string : null;
        }

        public static uint GetDockUId(ModuleDockingNode n)
        {
            if (UidField == null || UidField.FieldType != typeof(uint)) return 0;
            return (uint)UidField.GetValue(n);
        }

        /// <summary>Обнуляет идентификаторы парного порта (dockUId, dockedPartUId), если такие поля есть.</summary>
        public static void ClearUids(ModuleDockingNode n)
        {
            if (UidField != null && UidField.FieldType == typeof(uint))
                UidField.SetValue(n, 0u);
            if (DockedPartUidField != null && DockedPartUidField.FieldType == typeof(uint))
                DockedPartUidField.SetValue(n, 0u);
        }

        public static void SetOtherNode(ModuleDockingNode n, ModuleDockingNode other)
        {
            if (OtherNodeField != null && OtherNodeField.FieldType.IsAssignableFrom(typeof(ModuleDockingNode)))
                OtherNodeField.SetValue(n, other);
        }

        /// <summary>Обнуляет дальности захвата (порт не сможет «схватить» соседа). Возвращает прежние значения для RestoreCapture.</summary>
        public static float[] DisableCapture(ModuleDockingNode n)
        {
            float[] saved = new float[CaptureFields.Length];
            for (int i = 0; i < CaptureFields.Length; i++)
            {
                saved[i] = (float)CaptureFields[i].GetValue(n);
                CaptureFields[i].SetValue(n, 0f);
            }
            return saved;
        }

        public static void RestoreCapture(ModuleDockingNode n, float[] saved)
        {
            if (n == null || saved == null) return;
            for (int i = 0; i < CaptureFields.Length && i < saved.Length; i++)
                CaptureFields[i].SetValue(n, saved[i]);
        }

        /// <summary>Переводит порт в указанное состояние. true — если конечный автомат удалось перезапустить.</summary>
        public static bool SetState(ModuleDockingNode n, string stateName)
        {
            bool started = false;

            object fsm = FsmField != null ? FsmField.GetValue(n) : null;
            if (fsm != null)
            {
                MethodInfo m = fsm.GetType().GetMethod("StartFSM", Flags, null, new Type[] { typeof(string) }, null);
                if (m != null)
                {
                    try
                    {
                        m.Invoke(fsm, new object[] { stateName });
                        started = true;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[DockingPortRepair] StartFSM(\"" + stateName + "\") не удалось: " + e.Message);
                    }
                }
            }

            if (StateField != null && StateField.FieldType == typeof(string))
                StateField.SetValue(n, stateName);

            return started;
        }

        public static bool IsDocked(string s)
        {
            return !string.IsNullOrEmpty(s) && s.StartsWith("Docked", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPreAttached(string s)
        {
            return !string.IsNullOrEmpty(s) && s.StartsWith("PreAttached", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSameVessel(string s)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf("same vessel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int Role(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (s.IndexOf("docker", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (s.IndexOf("dockee", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }

        /// <summary>Здоровая пара: один порт «docker», другой «dockee».</summary>
        public static bool IsSyncedPair(string a, string b)
        {
            int ra = Role(a), rb = Role(b);
            return (ra == 1 && rb == 2) || (ra == 2 && rb == 1);
        }
    }
}
