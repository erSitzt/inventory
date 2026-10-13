using System;
using System.Collections.Generic;

namespace Inventory.Contracts
{
    /// <summary>
    /// Entscheidet während eines Jobs, welche Voraussetzungen übersprungen werden. Die Erkennung läuft genau einmal je Gruppe,
    /// vor ihrem ersten Schritt: danach würde schon der erste Schritt sie erfüllen und die übrigen fielen weg.
    /// </summary>
    public sealed class JobGroups
    {
        private readonly Dictionary<int, JobGroup> _groups = new Dictionary<int, JobGroup>();
        private readonly Dictionary<int, bool> _present = new Dictionary<int, bool>();
        private readonly Func<StepConditions, string?> _whyNot;

        /// <param name="whyNot">Prüft eine Erkennung auf dem Rechner: null = trifft zu, sonst der Grund.</param>
        public JobGroups(JobManifest manifest, Func<StepConditions, string?> whyNot)
        {
            _whyNot = whyNot;
            foreach (var group in manifest.Groups)
                _groups[group.Id] = group;
            foreach (var id in manifest.StartedGroups)
                _present[id] = false;
        }

        /// <summary>Die Gruppe des Schritts und ihre äußeren Gruppen, von innen nach außen.</summary>
        public static List<int> Chain(IEnumerable<JobGroup> groups, int? id)
        {
            var byId = new Dictionary<int, JobGroup>();
            foreach (var group in groups)
                byId[group.Id] = group;
            return Chain(byId, id);
        }

        private static List<int> Chain(Dictionary<int, JobGroup> groups, int? id)
        {
            var chain = new List<int>();
            // Die Längenbegrenzung fängt einen Zyklus in einem fehlerhaften Manifest ab.
            while (id != null && groups.TryGetValue(id.Value, out var group) && chain.Count <= groups.Count)
            {
                chain.Add(group.Id);
                id = group.Parent;
            }
            return chain;
        }

        /// <summary>Name der vorhandenen Voraussetzung, deretwegen der Schritt entfällt; null = ausführen.</summary>
        public string? Present(JobStep step)
        {
            var chain = Chain(_groups, step.Group);
            // Von außen nach innen: ist die äußere Voraussetzung da, wird die innere gar nicht erst geprüft.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (IsPresent(chain[i]))
                    return _groups[chain[i]].Name;
            }
            return null;
        }

        private bool IsPresent(int id)
        {
            if (_present.TryGetValue(id, out var known))
                return known;
            var detection = _groups[id].Detection;
            var present = detection != null && !detection.IsEmpty && _whyNot(detection) == null;
            _present[id] = present;
            return present;
        }

        /// <summary>Zu Beginn eines Folgejobs: prüft die Voraussetzungen nach, die mit dem Neustart endeten.</summary>
        public string? VerifyFinished(IEnumerable<int> finished)
        {
            foreach (var id in finished)
            {
                if (_groups.TryGetValue(id, out var group) && Unverified(group) is string error)
                    return error;
            }
            return null;
        }

        private string? Unverified(JobGroup group)
        {
            if (!group.Verify || group.Detection == null || group.Detection.IsEmpty)
                return null;
            return _whyNot(group.Detection) is string missing
                ? "Prerequisite \"" + group.Name + "\" was not detected after its steps: " + missing
                : null;
        }

        /// <summary>
        /// Nach einem ausgeführten Schritt: endet mit ihm eine Voraussetzung, die ihre Erkennung danach erfüllen muss,
        /// und tut sie es nicht, liefert dies die Fehlermeldung.
        /// </summary>
        /// <param name="next">Der folgende Schritt; null nach dem letzten.</param>
        public string? VerifyFinished(JobStep step, JobStep? next)
        {
            var open = next == null ? new List<int>() : Chain(_groups, next.Group);
            foreach (var id in Chain(_groups, step.Group))
            {
                if (open.Contains(id))
                    continue;
                if (Unverified(_groups[id]) is string error)
                    return error;
            }
            return null;
        }
    }
}
