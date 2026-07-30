/**
 * graph.js — Mermaid rendering with SSE-driven live node state updates (§12.2).
 *
 * Renders the Mermaid graph once, then applies overlay CSS classes by node id.
 * Classes are mutated in place as SSE events arrive — the graph is NEVER re-rendered,
 * which preserves pan/zoom state and meets the NFR-1.21 500ms budget.
 */

(function () {
    'use strict';

    // Initialize Mermaid with accessible defaults.
    if (typeof mermaid !== 'undefined') {
        mermaid.initialize({
            startOnLoad: true,
            theme: 'default',
            securityLevel: 'loose',
            flowchart: { useMaxWidth: true, htmlLabels: true }
        });
    }

    /**
     * After Mermaid renders, tag each node element with a `data-node-id` attribute
     * so `setNodeState` can target them without re-rendering.
     */
    function tagNodes() {
        document.querySelectorAll('.mermaid .node').forEach(function (node) {
            // Mermaid assigns IDs like "flowchart-executorId-0".
            var id = node.id || '';
            var match = id.match(/flowchart-(.+)-\d+$/);
            if (match) {
                node.setAttribute('data-node-id', match[1]);
            }
        });
    }

    // Run after Mermaid renders.
    if (typeof mermaid !== 'undefined') {
        mermaid.run().then(tagNodes).catch(function () { /* Mermaid not ready yet */ });
    }

    var nodeStates = ['executing', 'completed', 'failed', 'awaiting-approval', 'pending', 'skipped'];

    /**
     * Sets the visual state of a graph node.
     * @param {string} executorId - The executor ID (node key).
     * @param {string} state - One of: executing, completed, failed, awaiting-approval, pending, skipped.
     */
    window.setNodeState = function (executorId, state) {
        var node = document.querySelector('[data-node-id="' + CSS.escape(executorId) + '"]');
        if (!node) return;

        nodeStates.forEach(function (s) { node.classList.remove(s); });
        node.classList.add(state);

        // Accessibility: state must not be conveyed by color alone (NFR).
        node.setAttribute('aria-label', executorId + ': ' + state);
    };

    /**
     * Highlights a traversed edge between two nodes.
     */
    window.setEdgeTraversed = function (fromId, toId) {
        // Mermaid edge elements are harder to target; this is a best-effort implementation.
        document.querySelectorAll('.mermaid .edgePath').forEach(function (edge) {
            // Edge IDs follow patterns like "L-executorA-executorB"
            if (edge.id && edge.id.includes(fromId) && edge.id.includes(toId)) {
                edge.classList.add('traversed');
            }
        });
    };
})();
