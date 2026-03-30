(function () {
  const host = window.chrome && window.chrome.webview;
  const emptyState = document.getElementById("emptyState");
  const workspace = document.getElementById("workspace");
  const timelineScroll = document.getElementById("timelineScroll");
  const timelineCanvas = document.getElementById("timelineCanvas");
  const ruler = document.getElementById("ruler");
  const previewLayer = document.getElementById("previewLayer");
  const segmentLayer = document.getElementById("segmentLayer");
  const playhead = document.getElementById("playhead");
  const playPauseButton = document.getElementById("playPauseButton");
  const splitButton = document.getElementById("splitButton");
  const deleteButton = document.getElementById("deleteButton");
  const exportButton = document.getElementById("exportButton");
  const modeBadge = document.getElementById("modeBadge");
  const zoomBadge = document.getElementById("zoomBadge");
  const selectionLabel = document.getElementById("selectionLabel");
  const statusLabel = document.getElementById("statusLabel");

  const state = {
    durationSeconds: 0,
    currentPositionSeconds: 0,
    zoomFactor: 1,
    mode: "keep",
    isPlaying: false,
    segments: [],
    previewSegments: [],
    selectedSegmentIndex: -1,
    statusText: ""
  };

  const DRAG_ACTIVATION_PX = 4;
  const SNAP_THRESHOLD_PX = 12;
  const FRAME_STEP_SECONDS = 1 / 30;
  const SECOND_STEP_SECONDS = 1;
  const COARSE_STEP_SECONDS = 5;
  let interaction = null;
  let pendingZoomAnchor = null;

  function post(type, payload) {
    if (!host) {
      return;
    }

    host.postMessage({ type, payload: payload || {} });
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }

  function formatTime(totalSeconds) {
    const safe = Math.max(0, totalSeconds || 0);
    const hours = Math.floor(safe / 3600);
    const minutes = Math.floor((safe % 3600) / 60);
    const seconds = Math.floor(safe % 60);
    const frames = Math.floor((safe - Math.floor(safe)) * 100);
    if (hours > 0) {
      return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${String(frames).padStart(2, "0")}`;
    }

    return `${minutes}:${String(seconds).padStart(2, "0")}.${String(frames).padStart(2, "0")}`;
  }

  function getWorkWidth() {
    const viewportWidth = Math.max(timelineScroll.clientWidth - 40, 900);
    const pxPerSecond = 64 * Math.max(1, state.zoomFactor || 1);
    return Math.max(viewportWidth, (state.durationSeconds || 1) * pxPerSecond);
  }

  function timeToX(seconds) {
    if (state.durationSeconds <= 0) {
      return 20;
    }

    return 20 + (clamp(seconds, 0, state.durationSeconds) / state.durationSeconds) * (getWorkWidth() - 40);
  }

  function secondsPerPixel() {
    if (state.durationSeconds <= 0) {
      return 0;
    }

    return state.durationSeconds / Math.max(1, getWorkWidth() - 40);
  }

  function xToTime(clientX) {
    const rect = timelineCanvas.getBoundingClientRect();
    const local = clientX - rect.left;
    const normalized = clamp((local - 20) / Math.max(1, getWorkWidth() - 40), 0, 1);
    return normalized * state.durationSeconds;
  }

  function renderRuler() {
    ruler.innerHTML = "";
    if (state.durationSeconds <= 0) {
      return;
    }

    const workWidth = getWorkWidth();
    const maxMarks = 12;
    const step = state.durationSeconds / maxMarks;
    for (let i = 0; i <= maxMarks; i += 1) {
      const seconds = i * step;
      const x = 20 + (seconds / state.durationSeconds) * (workWidth - 40);
      const mark = document.createElement("div");
      mark.className = "ruler-mark";
      mark.style.left = `${x}px`;
      ruler.appendChild(mark);

      const label = document.createElement("div");
      label.className = "ruler-label";
      label.style.left = `${x}px`;
      label.textContent = formatTime(seconds);
      ruler.appendChild(label);
    }
  }

  function renderPreviewSegments() {
    previewLayer.innerHTML = "";
    state.previewSegments.forEach((segment) => {
      const el = document.createElement("div");
      el.className = `preview-track${state.mode === "delete" ? " delete" : ""}`;
      el.style.left = `${timeToX(segment.startSeconds)}px`;
      el.style.width = `${Math.max(6, timeToX(segment.endSeconds) - timeToX(segment.startSeconds))}px`;
      previewLayer.appendChild(el);
    });
  }

  function renderSegments() {
    segmentLayer.innerHTML = "";
    state.segments.forEach((segment, index) => {
      const segmentElement = document.createElement("div");
      segmentElement.className = `segment${index === state.selectedSegmentIndex ? " selected" : ""}${state.mode === "delete" ? " delete-mode" : ""}`;
      segmentElement.style.left = `${timeToX(segment.startSeconds)}px`;
      segmentElement.style.width = `${Math.max(28, timeToX(segment.endSeconds) - timeToX(segment.startSeconds))}px`;

      const core = document.createElement("div");
      core.className = "segment-core";
      core.tabIndex = 0;
      core.dataset.segmentIndex = String(index);
      core.innerHTML = `
        <div class="segment-title">片段 ${index + 1}</div>
        <div class="segment-times">${formatTime(segment.startSeconds)} - ${formatTime(segment.endSeconds)}</div>
      `;

      core.addEventListener("pointerdown", (event) => beginInteraction(event, index, "move"));

      const startHandle = document.createElement("div");
      startHandle.className = "segment-handle start";
      startHandle.addEventListener("pointerdown", (event) => beginInteraction(event, index, "trimStart"));

      const endHandle = document.createElement("div");
      endHandle.className = "segment-handle end";
      endHandle.addEventListener("pointerdown", (event) => beginInteraction(event, index, "trimEnd"));

      segmentElement.appendChild(core);
      segmentElement.appendChild(startHandle);
      segmentElement.appendChild(endHandle);
      segmentLayer.appendChild(segmentElement);
    });
  }

  function renderPlayhead() {
    playhead.style.left = `${timeToX(state.currentPositionSeconds)}px`;
  }

  function renderMeta() {
    modeBadge.textContent = state.mode === "delete" ? "删除模式" : "保留模式";
    zoomBadge.textContent = `${Math.round((state.zoomFactor || 1) * 100)}%`;
    playPauseButton.textContent = state.isPlaying ? "暂停" : "播放";

    const segment = state.selectedSegmentIndex >= 0 && state.selectedSegmentIndex < state.segments.length
      ? state.segments[state.selectedSegmentIndex]
      : null;
    selectionLabel.textContent = segment
      ? `片段 ${state.selectedSegmentIndex + 1}  ${formatTime(segment.startSeconds)} - ${formatTime(segment.endSeconds)}`
      : "未选中片段";
    statusLabel.textContent = state.statusText || "拖动边界修剪，拖动主体移动，单击只选中。";
  }

  function render() {
    const ready = state.durationSeconds > 0;
    emptyState.classList.toggle("hidden", ready);
    workspace.classList.toggle("hidden", !ready);
    if (!ready) {
      return;
    }

    timelineCanvas.style.width = `${getWorkWidth()}px`;
    renderRuler();
    renderPreviewSegments();
    renderSegments();
    renderPlayhead();
    renderMeta();
  }

  function updateState(payload) {
    const nextPayload = payload || {};
    const hasPositionUpdate = Object.prototype.hasOwnProperty.call(nextPayload, "currentPositionSeconds");
    const previousZoomFactor = state.zoomFactor;
    Object.assign(state, payload || {});
    render();

    if (pendingZoomAnchor && previousZoomFactor !== state.zoomFactor) {
      const anchorX = timeToX(pendingZoomAnchor.seconds);
      timelineScroll.scrollLeft = Math.max(0, anchorX - pendingZoomAnchor.viewportOffsetPx);
      pendingZoomAnchor = null;
    }

    if (hasPositionUpdate && interaction?.mode !== "scrub" && interaction?.mode !== "pan") {
      ensureTimeVisible(state.currentPositionSeconds);
    }
  }

  function focusTimeline() {
    if (typeof timelineScroll.focus === "function") {
      timelineScroll.focus({ preventScroll: true });
    }
  }

  function captureTimelinePointer(pointerId) {
    if (typeof timelineScroll.setPointerCapture !== "function") {
      return;
    }

    try {
      timelineScroll.setPointerCapture(pointerId);
    } catch {
      // Ignore pointer capture failures so selection and keyboard still work.
    }
  }

  function releaseTimelinePointer(pointerId) {
    if (typeof timelineScroll.releasePointerCapture !== "function") {
      return;
    }

    try {
      if (timelineScroll.hasPointerCapture(pointerId)) {
        timelineScroll.releasePointerCapture(pointerId);
      }
    } catch {
      // Ignore pointer release failures.
    }
  }

  function isEditableTarget(target) {
    return target instanceof HTMLInputElement
      || target instanceof HTMLTextAreaElement
      || target instanceof HTMLSelectElement
      || (target instanceof HTMLElement && target.isContentEditable);
  }

  function ensureTimeVisible(seconds, align = "auto") {
    if (state.durationSeconds <= 0) {
      return;
    }

    const x = timeToX(seconds);
    const visibleStart = timelineScroll.scrollLeft;
    const visibleEnd = visibleStart + timelineScroll.clientWidth;
    const margin = 120;

    if (align === "center") {
      timelineScroll.scrollLeft = Math.max(0, x - (timelineScroll.clientWidth / 2));
      return;
    }

    if (x < visibleStart + margin) {
      timelineScroll.scrollLeft = Math.max(0, x - margin);
    } else if (x > visibleEnd - margin) {
      timelineScroll.scrollLeft = Math.max(0, x - timelineScroll.clientWidth + margin);
    }
  }

  function getSnapThresholdSeconds() {
    return SNAP_THRESHOLD_PX * secondsPerPixel();
  }

  function findNearestSnapValue(targetSeconds, candidates) {
    const threshold = getSnapThresholdSeconds();
    if (threshold <= 0) {
      return targetSeconds;
    }

    let bestValue = targetSeconds;
    let bestDistance = Number.POSITIVE_INFINITY;
    for (const candidate of candidates) {
      if (!Number.isFinite(candidate)) {
        continue;
      }

      const distance = Math.abs(candidate - targetSeconds);
      if (distance <= threshold && distance < bestDistance) {
        bestValue = candidate;
        bestDistance = distance;
      }
    }

    return bestValue;
  }

  function getSnappedTime(segmentIndex, mode, proposedSeconds) {
    const segment = state.segments[segmentIndex];
    if (!segment) {
      return proposedSeconds;
    }

    const segmentDuration = Math.max(0, segment.endSeconds - segment.startSeconds);
    const previousSegment = segmentIndex > 0 ? state.segments[segmentIndex - 1] : null;
    const nextSegment = segmentIndex < state.segments.length - 1 ? state.segments[segmentIndex + 1] : null;
    const playhead = clamp(state.currentPositionSeconds, 0, state.durationSeconds);

    if (mode === "trimStart") {
      return findNearestSnapValue(proposedSeconds, [
        0,
        playhead,
        previousSegment ? previousSegment.endSeconds : NaN
      ]);
    }

    if (mode === "trimEnd") {
      return findNearestSnapValue(proposedSeconds, [
        state.durationSeconds,
        playhead,
        nextSegment ? nextSegment.startSeconds : NaN
      ]);
    }

    if (mode === "move") {
      return findNearestSnapValue(proposedSeconds, [
        0,
        playhead,
        playhead - segmentDuration,
        previousSegment ? previousSegment.endSeconds : NaN,
        nextSegment ? nextSegment.startSeconds - segmentDuration : NaN,
        state.durationSeconds - segmentDuration
      ]);
    }

    return proposedSeconds;
  }

  function stepCurrentPosition(direction, event) {
    const baseStep = event.ctrlKey
      ? COARSE_STEP_SECONDS
      : event.shiftKey
        ? SECOND_STEP_SECONDS
        : FRAME_STEP_SECONDS;
    const nextPosition = clamp(
      state.currentPositionSeconds + (direction * baseStep),
      0,
      state.durationSeconds
    );
    post("requestSeek", { positionSeconds: nextPosition });
  }

  function beginInteraction(event, segmentIndex, mode) {
    event.preventDefault();
    event.stopPropagation();

    const segment = state.segments[segmentIndex];
    if (!segment) {
      return;
    }

    const pointerId = event.pointerId;
    interaction = {
      pointerId,
      segmentIndex,
      mode,
      originClientX: event.clientX,
      dragActivated: false,
      originalStartSeconds: segment.startSeconds,
      originalEndSeconds: segment.endSeconds
    };

    captureTimelinePointer(pointerId);
    focusTimeline();
    post("selectSegment", { segmentIndex });
  }

  function beginPlayheadScrub(event) {
    event.preventDefault();
    event.stopPropagation();
    interaction = {
      pointerId: event.pointerId,
      segmentIndex: -1,
      mode: "scrub",
      originClientX: event.clientX,
      dragActivated: true
    };
    captureTimelinePointer(event.pointerId);
    focusTimeline();
  }

  function beginPan(event) {
    event.preventDefault();
    event.stopPropagation();
    interaction = {
      pointerId: event.pointerId,
      segmentIndex: -1,
      mode: "pan",
      originClientX: event.clientX,
      originScrollLeft: timelineScroll.scrollLeft,
      dragActivated: true
    };
    timelineScroll.classList.add("panning");
    captureTimelinePointer(event.pointerId);
    focusTimeline();
  }

  function updateInteraction(event) {
    if (!interaction || interaction.pointerId !== event.pointerId || state.durationSeconds <= 0) {
      return;
    }

    if (interaction.mode !== "scrub" && !interaction.dragActivated) {
      const deltaX = Math.abs(event.clientX - interaction.originClientX);
      if (deltaX < DRAG_ACTIVATION_PX) {
        return;
      }

      interaction.dragActivated = true;
    }

    const timeAtPointer = xToTime(event.clientX);
    if (interaction.mode === "trimStart") {
      const snappedTime = getSnappedTime(interaction.segmentIndex, "trimStart", timeAtPointer);
      post("trimSegment", {
        segmentIndex: interaction.segmentIndex,
        edge: "start",
        positionSeconds: snappedTime
      });
      return;
    }

    if (interaction.mode === "trimEnd") {
      const snappedTime = getSnappedTime(interaction.segmentIndex, "trimEnd", timeAtPointer);
      post("trimSegment", {
        segmentIndex: interaction.segmentIndex,
        edge: "end",
        positionSeconds: snappedTime
      });
      return;
    }

    if (interaction.mode === "scrub") {
      post("requestSeek", { positionSeconds: timeAtPointer });
      return;
    }

    if (interaction.mode === "pan") {
      const deltaX = event.clientX - interaction.originClientX;
      timelineScroll.scrollLeft = Math.max(0, interaction.originScrollLeft - deltaX);
      return;
    }

    const deltaSeconds = xToTime(event.clientX) - xToTime(interaction.originClientX);
    const proposedStartSeconds = interaction.originalStartSeconds + deltaSeconds;
    const snappedStartSeconds = getSnappedTime(interaction.segmentIndex, "move", proposedStartSeconds);
    post("moveSegment", {
      segmentIndex: interaction.segmentIndex,
      startSeconds: snappedStartSeconds
    });
  }

  function endInteraction(event) {
    if (!interaction || interaction.pointerId !== event.pointerId) {
      return;
    }

    timelineScroll.classList.remove("panning");
    releaseTimelinePointer(event.pointerId);
    interaction = null;
    focusTimeline();
  }

  timelineScroll.addEventListener("pointerdown", (event) => {
    focusTimeline();

    if (event.button === 2) {
      beginPan(event);
      return;
    }

    if (event.target.closest(".segment")) {
      return;
    }

    post("requestSeek", { positionSeconds: xToTime(event.clientX) });
  });

  timelineScroll.addEventListener("pointermove", updateInteraction);
  timelineScroll.addEventListener("pointerup", endInteraction);
  timelineScroll.addEventListener("pointercancel", endInteraction);
  timelineScroll.addEventListener("contextmenu", (event) => event.preventDefault());

  timelineScroll.addEventListener("wheel", (event) => {
    if (!event.ctrlKey) {
      return;
    }

    event.preventDefault();
    const rect = timelineScroll.getBoundingClientRect();
    pendingZoomAnchor = {
      seconds: xToTime(event.clientX),
      viewportOffsetPx: clamp(event.clientX - rect.left, 0, timelineScroll.clientWidth)
    };
    const nextZoom = event.deltaY < 0 ? state.zoomFactor * 1.1 : state.zoomFactor / 1.1;
    post("setZoom", { zoomFactor: nextZoom });
  }, { passive: false });

  document.addEventListener("keydown", (event) => {
    if (isEditableTarget(event.target)) {
      return;
    }

    const key = (event.key || "").toLowerCase();
    if (key === " " || key === "spacebar") {
      event.preventDefault();
      post("requestPlayPause");
    } else if (key === "k") {
      event.preventDefault();
      post("splitAt", { positionSeconds: state.currentPositionSeconds });
    } else if (key === "delete" || key === "backspace") {
      event.preventDefault();
      post("deleteSegment", { segmentIndex: state.selectedSegmentIndex });
    } else if (key === "[") {
      if (state.selectedSegmentIndex < 0) {
        return;
      }

      event.preventDefault();
      post("trimSegment", {
        segmentIndex: state.selectedSegmentIndex,
        edge: "start",
        positionSeconds: state.currentPositionSeconds
      });
    } else if (key === "]") {
      if (state.selectedSegmentIndex < 0) {
        return;
      }

      event.preventDefault();
      post("trimSegment", {
        segmentIndex: state.selectedSegmentIndex,
        edge: "end",
        positionSeconds: state.currentPositionSeconds
      });
    } else if (key === "arrowleft") {
      event.preventDefault();
      stepCurrentPosition(-1, event);
    } else if (key === "arrowright") {
      event.preventDefault();
      stepCurrentPosition(1, event);
    }
  });

  playPauseButton.addEventListener("click", () => {
    post("requestPlayPause");
    focusTimeline();
  });
  splitButton.addEventListener("click", () => {
    post("splitAt", { positionSeconds: state.currentPositionSeconds });
    focusTimeline();
  });
  deleteButton.addEventListener("click", () => {
    post("deleteSegment", { segmentIndex: state.selectedSegmentIndex });
    focusTimeline();
  });
  exportButton.addEventListener("click", () => {
    post("requestExport");
    focusTimeline();
  });
  playhead.addEventListener("pointerdown", beginPlayheadScrub);

  if (host) {
    host.addEventListener("message", (event) => {
      const message = event.data || {};
      if (message.type === "timelineState") {
        updateState(message.payload);
      } else if (message.type === "focusPlayhead") {
        ensureTimeVisible(state.currentPositionSeconds, "center");
        focusTimeline();
      }
    });

    post("ready");
  } else {
    updateState({
      durationSeconds: 90,
      currentPositionSeconds: 18,
      zoomFactor: 1.2,
      mode: "keep",
      isPlaying: false,
      selectedSegmentIndex: 0,
      statusText: "本地预览模式，未连接 WinUI 宿主。",
      segments: [
        { startSeconds: 5, endSeconds: 24 },
        { startSeconds: 31, endSeconds: 58 }
      ],
      previewSegments: [
        { startSeconds: 5, endSeconds: 24 },
        { startSeconds: 31, endSeconds: 58 }
      ]
    });
  }
})();
