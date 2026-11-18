#!/usr/bin/env python3
"""Check benchmark accounting without launching Godot."""

import unittest

from analyze_trial00_progress import distribution, frame_boundaries, input_stats, stats
from analyze_trial00_scheduler import attribute


class Trial00AnalysisTests(unittest.TestCase):
    def test_scheduler_clips_overlap_and_does_not_reuse_a_nearby_wait_stack(self):
        states = [{'start': 10, 'duration': 8, 'state': 'Blocked'},
                  {'start': 18, 'duration': 1, 'state': 'Running'},
                  {'start': 19, 'duration': 9, 'state': 'Blocked'}]
        stacks = {10.001: ['-[IOSurfaceSharedEvent waitUntilSignaledValue:timeoutMS:]']}
        self.assertEqual({'metalSharedEvent': 3, 'Running': 1, 'unsampledBlocked': 1},
                         attribute(15, 20, states, stacks, sorted(stacks)))

    def test_boundaries_pair_the_preceding_submission_and_reject_nonmatching_ticks(self):
        row = dict(line='8', focused='True', time_ms='10', gap_ms='10', pre_draw_ms='2',
                   post_draw_ms='4', pre_draw_count='1', post_draw_count='1', gc_pause_total_ms='3')
        result = frame_boundaries([row, dict(row, time_ms='40', gap_ms='30', pre_draw_ms='12',
                                            post_draw_ms='33', gc_pause_total_ms='4'),
                                   dict(row, time_ms='52', pre_draw_count='2'), dict(row, time_ms='65')])
        self.assertEqual((1, 2), (result['matched'], result['unmatched']))
        self.assertEqual({'preToPostMs': 1}, result['over16_67Dominant'])
        self.assertEqual(7, result['slowest'][0]['postToProcessMs'])
        self.assertEqual(1, result['slowest'][0]['gcPauseMs'])
        self.assertIsNone(frame_boundaries([{'line': '8'}]))

    def test_distribution_ignores_invalid_timings_and_keeps_zero(self):
        self.assertEqual(distribution([float("nan"), -1, float("inf")]), {"count": 0})
        result = distribution([0, 5, 10])
        self.assertEqual(result["count"], 3)
        self.assertEqual(result["p50Ms"], 5)
        self.assertEqual(result["maxMs"], 10)

    def test_budget_allocation_memory_and_gc_accounting(self):
        rows = [{"gap_ms": str(gap), "time_ms": str(10 + index * 20),
                 "focused": str(index == 0), "allocated_bytes": str(index * 1048576),
                 "gc0": str(index), "gc1": "0", "gc2": "0", "texture_mib": "NaN",
                 "rss_bytes": str(2 * 1048576), "managed_bytes": str(1048576),
                 "video_bytes": str(3 * 1048576), "gpu_ms": "0"}
                for index, gap in enumerate([8, 20])]
        result = stats(rows)
        self.assertEqual(result["over8_33"], 1)
        self.assertEqual(result["over16_67"], 1)
        self.assertEqual(result["focusedPercent"], 50)
        self.assertEqual(result["allocatedMiBPerSecond"], 50)
        self.assertEqual(result["gcCollections"]["gc0"], 1)
        self.assertEqual(result["rssMiB"]["peak"], 2)
        self.assertEqual(result["videoMiB"]["peak"], 3)
        self.assertNotIn("gpu_ms", result)
        self.assertEqual(stats([]), {})

    def test_missing_response_is_counted_and_unfocused_latency_excluded(self):
        samples = [
            {"Kind": "hurry", "InjectedMs": 100, "DispatchedMs": 110, "ResponseMs": 120,
             "ContentPostDrawMs": 125, "Focused": True},
            {"Kind": "hurry", "InjectedMs": 200, "DispatchedMs": 250, "Focused": False,
             "TimedOut": True},
            {"Kind": "advance", "InjectedMs": 300, "Focused": True, "TerminalOptions": True},
        ]
        result = input_stats(samples)
        self.assertEqual(result["unhandled"], 1)
        self.assertEqual(result["unacknowledged"], 2)
        self.assertEqual(result["timedOut"], 1)
        self.assertEqual(result["terminalOptions"], 1)
        dispatch = result["latencyByKind"]["hurry"]["dispatch"]
        self.assertEqual(dispatch["count"], 1)
        self.assertEqual(dispatch["p50Ms"], 10)
        self.assertEqual(result["latencyByKind"]["advance"]["contentPostDraw"], {"count": 0})


if __name__ == "__main__":
    unittest.main()
