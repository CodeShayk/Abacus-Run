/**
 * live.js — EventSource per detail page with polling fallback (§12.3).
 *
 * Opens one EventSource per instance. On SSE error, falls back to polling
 * `/events/history?from={lastEventId}` every 10s and shows a "live updates
 * unavailable" banner. The banner is required by FR-11.9: silently frozen state
 * is worse than visibly stale state.
 */

(function () {
    'use strict';

    var eventSource = null;
    var pollInterval = null;
    var lastEventId = 0;
    var instanceId = null;
    var banner = null;

    /**
     * Initializes live updates for an instance.
     * @param {string} id - The instance ID.
     * @param {boolean} isTerminal - If true, skip live updates (instance is done).
     */
    window.initLiveUpdates = function (id, isTerminal) {
        instanceId = id;

        if (isTerminal) return;

        banner = createBanner();
        startEventSource();
    };

    function startEventSource() {
        var url = '/instances/' + instanceId + '/events';
        if (lastEventId > 0) {
            url += '?lastEventId=' + lastEventId;
        }

        eventSource = new EventSource(url);

        eventSource.addEventListener('executor.invoked', function (e) {
            var data = JSON.parse(e.data);
            if (typeof setNodeState === 'function') setNodeState(data.executorId, 'executing');
            appendEventRow(e);
            lastEventId = parseInt(e.lastEventId || '0', 10) || lastEventId;
        });

        eventSource.addEventListener('executor.completed', function (e) {
            var data = JSON.parse(e.data);
            if (typeof setNodeState === 'function') setNodeState(data.executorId, 'completed');
            appendEventRow(e);
            lastEventId = parseInt(e.lastEventId || '0', 10) || lastEventId;
        });

        eventSource.addEventListener('executor.failed', function (e) {
            var data = JSON.parse(e.data);
            if (typeof setNodeState === 'function') setNodeState(data.executorId, 'failed');
            appendEventRow(e);
            lastEventId = parseInt(e.lastEventId || '0', 10) || lastEventId;
        });

        eventSource.addEventListener('approval.requested', function (e) {
            var data = JSON.parse(e.data);
            if (typeof setNodeState === 'function') setNodeState(data.executorId, 'awaiting-approval');
            showApprovalPrompt(data);
            appendEventRow(e);
            lastEventId = parseInt(e.lastEventId || '0', 10) || lastEventId;
        });

        eventSource.addEventListener('approval.decided', function (e) {
            var data = JSON.parse(e.data);
            if (typeof setNodeState === 'function') setNodeState(data.executorId, 'executing');
            appendEventRow(e);
            lastEventId = parseInt(e.lastEventId || '0', 10) || lastEventId;
        });

        eventSource.addEventListener('workflow.terminated', function (e) {
            appendEventRow(e);
            hideBanner();
            if (eventSource) eventSource.close();
        });

        eventSource.onerror = function () {
            // SSE connection failed; fall back to polling (FR-11.9).
            if (eventSource) eventSource.close();
            eventSource = null;
            showBanner();
            startPolling();
        };
    }

    function startPolling() {
        if (pollInterval) return;

        pollInterval = setInterval(function () {
            fetch('/instances/' + instanceId + '/events/history?from=' + lastEventId + '&limit=100')
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    if (data.items && data.items.length > 0) {
                        data.items.forEach(function (evt) {
                            lastEventId = Math.max(lastEventId, evt.sequence);
                            processPolledEvent(evt);
                        });
                    }
                })
                .catch(function () { /* silently retry next cycle */ });
        }, 10000);
    }

    function processPolledEvent(evt) {
        if (typeof setNodeState === 'function' && evt.executorId) {
            switch (evt.eventType) {
                case 'executor.invoked':      setNodeState(evt.executorId, 'executing'); break;
                case 'executor.completed':    setNodeState(evt.executorId, 'completed'); break;
                case 'executor.failed':       setNodeState(evt.executorId, 'failed'); break;
                case 'approval.requested':    setNodeState(evt.executorId, 'awaiting-approval'); break;
                case 'approval.decided':      setNodeState(evt.executorId, 'executing'); break;
            }
        }
    }

    function appendEventRow(sseEvent) {
        var tbody = document.getElementById('events-tbody');
        if (!tbody) return;

        try {
            var data = JSON.parse(sseEvent.data);
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td>' + (sseEvent.lastEventId || '—') + '</td>' +
                '<td><span class="event-type">' + (sseEvent.type || '') + '</span></td>' +
                '<td>' + (data.executorId || '—') + '</td>' +
                '<td>' + (data.superstep || '—') + '</td>' +
                '<td>' + new Date().toLocaleTimeString() + '</td>';
            tbody.appendChild(tr);
        } catch (e) { /* best effort */ }
    }

    function showApprovalPrompt(data) {
        // Simple notification; a production implementation would show a modal.
        var container = document.querySelector('.page-body');
        if (!container) return;

        var prompt = document.createElement('div');
        prompt.className = 'approval-prompt';
        prompt.setAttribute('role', 'alert');
        prompt.innerHTML =
            '<strong>Approval required</strong> for executor <em>' + data.executorId + '</em>' +
            (data.reason ? ': ' + data.reason : '') +
            ' <a href="/control/Approvals" class="btn btn-sm btn-success">Review</a>';
        container.prepend(prompt);
    }

    function createBanner() {
        var el = document.createElement('div');
        el.className = 'stale-banner';
        el.setAttribute('role', 'alert');
        el.innerHTML = '⚠️ Live updates unavailable — falling back to polling every 10s.';
        el.style.display = 'none';
        var header = document.querySelector('.page-header');
        if (header) header.after(el);
        return el;
    }

    function showBanner() { if (banner) banner.style.display = 'block'; }
    function hideBanner() { if (banner) banner.style.display = 'none'; }
})();
