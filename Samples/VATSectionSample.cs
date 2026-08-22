using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Four things a baked section can do, on one component, so there is something to look at before
     * writing any code. Drop it on any baked prefab that has sections, type a section name, pick a mode.
     *
     * It is also the worked example of the two ways to drive a section, which are different jobs:
     *
     *   GLANCE and RECOIL describe a transition ONCE and let the GPU walk it. Nothing runs per frame.
     *                     This is what almost everything should use.
     *
     *   LOOK_AT and SWAY  change every frame, because following a moving target and riding a sine wave
     *                     are not curves that can be described in advance.
     *
     * None of this is meant to ship. It is here to be read and then thrown away.
     */
    /// <summary>Example behaviours for a baked mesh section.</summary>
    [RequireComponent(typeof(VATSectionDriver))]
    public class VATSectionSample : MonoBehaviour
    {

        [Tooltip("Section to drive, as named in the baker. Leave empty to use the first baked one.")]
        [SerializeField] private string sectionName;

        [SerializeField] private VATSectionSampleMode mode = VATSectionSampleMode.LOOK_AT;

        [Header("Look At")]
        [Tooltip("What to follow. Usually the player.")]
        [SerializeField] private Transform target;

        [Tooltip("How hard it chases. Around 8 reads as a natural head turn.")]
        [SerializeField] private float sharpness = 8f;

        [Tooltip("Stop following past this distance and settle back.")]
        [SerializeField] private float noticeRange = 12f;

        [Header("Glance and Recoil")]
        [Tooltip("Seconds between glances.")]
        [SerializeField] private float interval = 4f;

        [Tooltip("How far it turns, in degrees.")]
        [SerializeField] private float angle = 40f;

        [Tooltip("Seconds the turn takes.")]
        [SerializeField] private float duration = .35f;

        [Header("Sway")]
        [Tooltip("Cycles per second.")]
        [SerializeField] private float swaySpeed = .6f;

        private VATSectionDriver _driver;
        private VATSectionSampleMode _activeMode;
        private string _activeSection;
        private float _nextTrigger;
        private bool _glancing;

        private string Section => string.IsNullOrEmpty(sectionName) && _driver && _driver.ClipSet
                                  && _driver.ClipSet.SectionCount > 0
            ? _driver.ClipSet.sections[0].name
            : sectionName;

        private void OnEnable()
        {
            _driver = GetComponent<VATSectionDriver>();
            _activeSection = null;
        }

        private void OnDisable()
        {
            if (_driver && !string.IsNullOrEmpty(Section)) _driver.Release(Section, .25f);
        }

        private void Update()
        {
            if (!_driver) return;

            string section = Section;
            if (string.IsNullOrEmpty(section) || !_driver.Has(section)) return;

            if (mode != _activeMode || section != _activeSection) Restart(section);

            switch (mode)
            {
                case VATSectionSampleMode.LOOK_AT: UpdateLookAt(section); break;
                case VATSectionSampleMode.GLANCE: UpdateGlance(section); break;
                case VATSectionSampleMode.RECOIL: UpdateRecoil(section); break;
                case VATSectionSampleMode.SWAY: UpdateSway(section); break;
            }
        }

        /*
         * Switching mode in the inspector has to put everything back, or the next mode inherits state
         * that makes no sense for it: a tracking loop still running in the background and fighting it,
         * a glance timer that expired minutes ago, a recoil settle still pending.
         *
         * The section snaps rather than eases back on purpose. A mode change is not something the
         * character is doing, so blending it would only make it harder to see where each mode starts.
         */
        private void Restart(string section)
        {
            if (!string.IsNullOrEmpty(_activeSection) && _driver.Has(_activeSection))
            {
                _driver.StopTracking(_activeSection);
                _driver.Release(_activeSection, 0f);
            }

            CancelInvoke(nameof(Settle));

            _activeMode = mode;
            _activeSection = section;
            _nextTrigger = Time.time + interval;
            _glancing = false;
        }

        /*
         * Track rather than TurnTo, because the target keeps moving and there is no curve to describe.
         * Stepping out of range hands it back to a timed transition, which is the point at which the
         * per frame work stops entirely.
         */
        private void UpdateLookAt(string section)
        {
            bool inRange = target && Vector3.Distance(target.position, transform.position) <= noticeRange;

            if (!inRange)
            {
                if (!_driver.IsTracking) return;

                _driver.StopTracking(section);
                _driver.Release(section, .5f);
                return;
            }

            _driver.Track(section, _driver.LookRotation(section, target.position), sharpness);
        }

        // Two writes per glance, and nothing at all in between.
        private void UpdateGlance(string section)
        {
            if (Time.time < _nextTrigger) return;

            if (_glancing)
            {
                _driver.Release(section, duration);
                _nextTrigger = Time.time + interval;
            }
            else
            {
                _driver.TurnTo(section,
                    new Vector3(Random.Range(-angle, angle) * .3f, Random.Range(-angle, angle), 0f),
                    duration);

                _nextTrigger = Time.time + (interval * .4f);
            }

            _glancing = !_glancing;
        }

        // Fires itself on the same interval, so the mode shows something without needing to be wired
        // to anything. Fire is still public for hooking to whatever actually pulls the trigger.
        private void UpdateRecoil(string section)
        {
            if (Time.time < _nextTrigger) return;

            Fire();
            _nextTrigger = Time.time + interval;
        }

        // Duration 0 every frame is the CPU driven path: the shader is handed a finished transition
        // rather than a running one, and no extra shader code exists for this case.
        private void UpdateSway(string section)
        {
            float phase = Time.time * swaySpeed * Mathf.PI * 2f;

            _driver.Set(section, Quaternion.Euler(
                Mathf.Sin(phase * .7f) * angle * .25f,
                Mathf.Sin(phase) * angle * .5f,
                0f), Vector3.zero, 0f);
        }

        /// <summary>
        /// Kicks the section and lets it settle. Wire this to whatever fires the weapon.
        /// </summary>
        public void Fire()
        {
            string section = Section;
            if (!_driver || string.IsNullOrEmpty(section) || !_driver.Has(section)) return;

            _driver.TurnTo(section, new Vector3(-angle, 0f, 0f), Mathf.Min(duration, .06f));
            Invoke(nameof(Settle), Mathf.Min(duration, .06f));
        }

        private void Settle()
        {
            string section = Section;
            if (!string.IsNullOrEmpty(section)) _driver.Release(section, duration * 3f);
        }

    }
}
