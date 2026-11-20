"""Frames in, Unified Expressions and eyes out: MediaPipe's face landmarker (bundled, run with onnxruntime) for the
whole face, and a Babble model the user already has, when given, for every mouth shape it names. Needs numpy and
onnxruntime."""

from __future__ import annotations

import json
import math
import os

import numpy as np
import onnxruntime as ort

from . import expressions

MODELS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Models")
LANDMARK_COUNT = 478


class ModelFiles:
    """The bundled Models folder, and what mediapipe.json says about the MediaPipe models."""

    def __init__(self, folder: str = MODELS):
        self.folder = folder
        for name in ("face_detector.onnx", "face_landmarks_detector.onnx", "face_blendshapes.onnx", "mediapipe.json"):
            if not os.path.isfile(os.path.join(folder, name)):
                raise FileNotFoundError("A bundled model file is missing: " + os.path.join(folder, name))
        with open(os.path.join(folder, "mediapipe.json"), encoding="utf-8") as file:
            root = json.load(file)
        self.blendshape_input_landmarks = [int(i) for i in root["blendshapeInputLandmarks"]]
        self.blendshape_names = [str(n) for n in root["blendshapeNames"]]
        self.detector = os.path.join(folder, "face_detector.onnx")
        self.landmarks = os.path.join(folder, "face_landmarks_detector.onnx")
        self.blendshapes = os.path.join(folder, "face_blendshapes.onnx")
        self.babble_labels = os.path.join(folder, "babble-labels.json")


def read_labels(path: str) -> list[str]:
    """The "labels" list of a Babble labels file: one name per model output, in output order."""
    with open(path, encoding="utf-8") as file:
        return [str(label) for label in json.load(file)["labels"]]


class Rect:
    """A rotated rectangle in a frame, in pixels: centre, size, and rotation in radians (y down)."""

    __slots__ = ("cx", "cy", "width", "height", "rotation")

    def __init__(self, cx, cy, width, height, rotation=0.0):
        self.cx, self.cy, self.width, self.height, self.rotation = cx, cy, width, height, rotation

    def to_frame(self, u, v):
        """Frame positions of points given in the rectangle's own 0..1 coordinates (numpy arrays)."""
        du, dv = (u - 0.5) * self.width, (v - 0.5) * self.height
        cos, sin = math.cos(self.rotation), math.sin(self.rotation)
        return self.cx + du * cos - dv * sin, self.cy + du * sin + dv * cos


def _sample(frame: np.ndarray, rect: Rect, out_width: int, out_height: int) -> np.ndarray:
    """A rotated rectangle of an RGB frame (H x W x 3, uint8) resampled to out_height x out_width x 3 (float32),
    bilinear; outside the frame is black."""
    height, width = frame.shape[:2]
    cos, sin = math.cos(rect.rotation), math.sin(rect.rotation)
    dv = ((np.arange(out_height, dtype=np.float32) + 0.5) / out_height - 0.5) * rect.height
    du = ((np.arange(out_width, dtype=np.float32) + 0.5) / out_width - 0.5) * rect.width
    du, dv = du[None, :], dv[:, None]
    x = rect.cx + du * cos - dv * sin - 0.5
    y = rect.cy + du * sin + dv * cos - 0.5
    x0, y0 = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = (x - x0)[..., None], (y - y0)[..., None]
    out = np.zeros((out_height, out_width, 3), dtype=np.float32)
    source = frame.astype(np.float32)
    for ox, oy, weight in ((0, 0, (1 - fx) * (1 - fy)), (1, 0, fx * (1 - fy)), (0, 1, (1 - fx) * fy), (1, 1, fx * fy)):
        px, py = x0 + ox, y0 + oy
        inside = (px >= 0) & (py >= 0) & (px < width) & (py < height)
        values = source[np.clip(py, 0, height - 1), np.clip(px, 0, width - 1)]
        out += np.where(inside[..., None], values * weight, 0.0)
    return out


def _session(path: str, threads: int) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = max(1, threads)
    options.inter_op_num_threads = 1
    return ort.InferenceSession(path, options, providers=["CPUExecutionProvider"])


class FaceDetector:
    """MediaPipe's short-range face detector (128 x 128 input, 896 anchors): the strongest face in a frame."""

    _SIZE = 128
    _ANCHORS = 896

    def __init__(self, path: str, threads: int):
        self._session = _session(path, threads)
        self._input = self._session.get_inputs()[0].name
        # A 16 x 16 grid (stride 8) with 2 anchors per cell, then an 8 x 8 grid with 6 per cell; anchors at cell centres.
        anchors = []
        for grid, per_cell in ((16, 2), (8, 6)):
            for y in range(grid):
                for x in range(grid):
                    anchors += [((x + 0.5) / grid, (y + 0.5) / grid)] * per_cell
        self._anchors = np.array(anchors, dtype=np.float32)

    def detect(self, frame: np.ndarray):
        """(score, cx, cy, width, height, keypoints[6][2]) in frame pixels, or None when no face scores 0.5 or more.
        Keypoints: right eye, left eye, nose tip, mouth, right ear, left ear (the subject's sides)."""
        height, width = frame.shape[:2]
        side = float(max(width, height))
        square = Rect(width / 2.0, height / 2.0, side, side)
        image = _sample(frame, square, self._SIZE, self._SIZE) / 127.5 - 1.0
        outputs = self._session.run(None, {self._input: image[None].astype(np.float32)})
        boxes = scores = None
        for output in outputs:
            flat = np.asarray(output, dtype=np.float32).reshape(-1)
            if flat.size == self._ANCHORS * 16:
                boxes = flat.reshape(self._ANCHORS, 16)
            elif flat.size == self._ANCHORS:
                scores = flat
        if boxes is None or scores is None:
            raise RuntimeError("The face detector's outputs are not the expected 896 x 16 boxes and 896 scores.")
        scores = 1.0 / (1.0 + np.exp(-np.clip(scores.astype(np.float64), -100, 100)))
        candidates = np.nonzero(scores >= 0.5)[0]
        if candidates.size == 0:
            return None
        candidates = candidates[np.argsort(-scores[candidates], kind="stable")]
        decoded = self._decode(boxes)
        corners = np.stack([decoded[:, 0] - decoded[:, 2] / 2, decoded[:, 1] - decoded[:, 3] / 2,
                            decoded[:, 0] + decoded[:, 2] / 2, decoded[:, 1] + decoded[:, 3] / 2], axis=1)
        best = corners[candidates[0]]
        # Weighted suppression of the best face only: every box overlapping it adds to its position by score.
        total = 0.0
        sum_ = np.zeros(16, dtype=np.float64)
        for index in candidates:
            if _overlap(best, corners[index]) < 0.3:
                continue
            total += scores[index]
            sum_ += decoded[index] * scores[index]
        sum_ /= total
        left, top = square.cx - side / 2, square.cy - side / 2
        keypoints = [(left + sum_[4 + k * 2] * side, top + sum_[5 + k * 2] * side) for k in range(6)]
        return (float(scores[candidates[0]]), left + sum_[0] * side, top + sum_[1] * side, sum_[2] * side, sum_[3] * side, keypoints)

    def _decode(self, boxes):
        """Every box in the input's 0..1: positions are offsets from the anchor, sizes are absolute."""
        decoded = boxes / self._SIZE
        decoded[:, 0] += self._anchors[:, 0]
        decoded[:, 1] += self._anchors[:, 1]
        decoded[:, 4::2] += self._anchors[:, 0:1]
        decoded[:, 5::2] += self._anchors[:, 1:2]
        return decoded


def _overlap(a, b) -> float:
    w = min(a[2], b[2]) - max(a[0], b[0])
    h = min(a[3], b[3]) - max(a[1], b[1])
    if w <= 0 or h <= 0:
        return 0.0
    shared = w * h
    return shared / ((a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - shared)


class FaceLandmarker:
    """MediaPipe's face landmarker: the detector finds the face, the landmark model places 478 points in a 256 x 256
    crop turned upright, and the blendshape model turns 146 of them into 52 ARKit-style values. While a face stays in
    view the next crop is taken from this frame's landmarks, and the detector runs again only when it is lost."""

    _SIZE = 256
    _ENLARGE = 1.5
    _RIGHT_EYE_OUTER, _LEFT_EYE_OUTER = 33, 263

    def __init__(self, files: ModelFiles, threads: int):
        self._files = files
        self._detector = FaceDetector(files.detector, threads)
        self._landmarks = _session(files.landmarks, threads)
        self._blendshapes = _session(files.blendshapes, threads)
        self._landmarks_input = self._landmarks.get_inputs()[0].name
        self._blendshapes_input = self._blendshapes.get_inputs()[0].name
        self._tracked: Rect | None = None

    def reset(self) -> None:
        self._tracked = None

    def process(self, frame: np.ndarray):
        """(landmarks 478 x 3 in frame pixels, blendshapes, roll) for the face in frame, or None (tracking reset)."""
        if self._tracked is not None:
            rect = self._tracked
        else:
            face = self._detector.detect(frame)
            if face is None:
                return None
            _, cx, cy, w, h, keypoints = face
            rotation = math.atan2(keypoints[1][1] - keypoints[0][1], keypoints[1][0] - keypoints[0][0])
            side = max(w, h) * self._ENLARGE
            rect = Rect(cx, cy, side, side, rotation)
        landmarks = self._run_landmarks(frame, rect)
        if landmarks is None:
            self._tracked = None
            return None
        self._tracked = self._from_landmarks(landmarks)
        roll = self._angle(landmarks)
        chosen = landmarks[self._files.blendshape_input_landmarks, :2].astype(np.float32)
        blendshapes = np.asarray(self._blendshapes.run(None, {self._blendshapes_input: chosen[None]})[0], dtype=np.float32).reshape(-1)
        return landmarks, blendshapes, roll

    def _angle(self, landmarks) -> float:
        a, b = landmarks[self._RIGHT_EYE_OUTER], landmarks[self._LEFT_EYE_OUTER]
        return math.atan2(b[1] - a[1], b[0] - a[0])

    def _from_landmarks(self, landmarks) -> Rect:
        """The crop for the next frame: the landmarks' bounds measured along the face's own axes, enlarged 1.5 times."""
        rotation = self._angle(landmarks)
        cos, sin = math.cos(rotation), math.sin(rotation)
        x, y = landmarks[:, 0], landmarks[:, 1]
        u, v = x * cos + y * sin, -x * sin + y * cos
        cu, cv = (u.min() + u.max()) / 2, (v.min() + v.max()) / 2
        side = max(u.max() - u.min(), v.max() - v.min()) * self._ENLARGE
        return Rect(cu * cos - cv * sin, cu * sin + cv * cos, side, side, rotation)

    def _run_landmarks(self, frame, rect: Rect):
        image = _sample(frame, rect, self._SIZE, self._SIZE) / 255.0
        outputs = self._landmarks.run(None, {self._landmarks_input: image[None].astype(np.float32)})
        coordinates = None
        presence = None
        for output in outputs:
            flat = np.asarray(output, dtype=np.float32).reshape(-1)
            if flat.size == LANDMARK_COUNT * 3:
                coordinates = flat.reshape(LANDMARK_COUNT, 3)
            elif flat.size == 1 and presence is None:
                # Of the two single values, the face presence is the first output after the landmarks.
                presence = float(flat[0])
        if coordinates is None:
            raise RuntimeError("The landmark model's output is not 478 x 3 values.")
        if presence is not None and 1.0 / (1.0 + math.exp(-presence)) < 0.5:
            return None
        x, y = rect.to_frame(coordinates[:, 0] / self._SIZE, coordinates[:, 1] / self._SIZE)
        return np.stack([x, y, coordinates[:, 2] / self._SIZE * rect.width], axis=1)


class BabbleModel:
    """A Babble face model the user already has (never bundled), run on the mouth of an ordinary camera image: a square
    around the mouth, turned upright with the face, in grey, scaled to the model's input and divided by 255."""

    _UPPER_LIP, _LOWER_LIP, _CHIN, _RIGHT_CHEEK, _LEFT_CHEEK = 13, 14, 152, 205, 425

    def __init__(self, model: str, labels: str, threads: int, crop_scale: float = 1.0):
        if not os.path.isfile(model):
            raise FileNotFoundError("The Babble model was not found: " + model)
        self.labels = read_labels(labels)
        self.crop_scale = crop_scale if crop_scale > 0 else 1.0
        self._session = _session(model, threads)
        description = self._session.get_inputs()[0]
        shape = description.shape
        if len(shape) != 4 or not all(isinstance(d, int) and d > 0 for d in shape[1:]):
            raise ValueError("The Babble model's input is not [1, channels, height, width].")
        self._input = description.name
        self._channels, self._height, self._width = shape[1], shape[2], shape[3]

    def run(self, frame, landmarks, roll) -> list[float]:
        """The model's values for the mouth, one per label (extra outputs dropped, missing ones 0)."""
        l = landmarks
        cx = (l[self._UPPER_LIP, 0] + l[self._LOWER_LIP, 0] + l[self._CHIN, 0]) / 3
        cy = (l[self._UPPER_LIP, 1] + l[self._LOWER_LIP, 1] + l[self._CHIN, 1]) / 3
        cheeks = math.hypot(l[self._LEFT_CHEEK, 0] - l[self._RIGHT_CHEEK, 0], l[self._LEFT_CHEEK, 1] - l[self._RIGHT_CHEEK, 1])
        side = max(1.0, cheeks * 2 * self.crop_scale)
        rgb = _sample(frame, Rect(cx, cy, side, side, roll), self._width, self._height)
        gray = (0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]) / 255.0
        tensor = np.repeat(gray[None, None], self._channels, axis=1).astype(np.float32)
        raw = np.asarray(self._session.run(None, {self._input: tensor})[0], dtype=np.float32).reshape(-1)
        values = [0.0] * len(self.labels)
        for i in range(min(len(raw), len(values))):
            values[i] = float(raw[i])
        return values


class FaceTracker:
    """MediaPipe for the whole face; a Babble model, when given, replaces MediaPipe's value for every shape it names."""

    def __init__(self, babble_model: str | None, babble_labels: str | None, babble_crop: float, threads: int):
        self._files = ModelFiles()
        self._landmarker = FaceLandmarker(self._files, threads)
        self._babble = None
        if babble_model:
            self._babble = BabbleModel(babble_model, babble_labels or self._files.babble_labels, threads, babble_crop)

    @property
    def uses_babble(self) -> bool:
        return self._babble is not None

    def track(self, frame: np.ndarray, unified: list[float], eye: list[float]) -> bool:
        """Fills unified and eye for the face in frame; False (both left as they were) when there is none."""
        found = self._landmarker.process(frame)
        if found is None:
            return False
        landmarks, blendshapes, roll = found
        expressions.from_arkit(self._files.blendshape_names, blendshapes, unified, eye)
        if self._babble is not None:
            for label, value in zip(self._babble.labels, self._babble.run(frame, landmarks, roll)):
                expressions.set_value(label, value, unified)
        return True

# ============================================================================
# GLOSSARY: face tracking
# ============================================================================
#
# face (this file)         MediaPipe (bundled ONNX in Models/) and an optional Babble model, as the C# module runs them.
#   ModelFiles               Models/: face_detector, face_landmarks_detector, face_blendshapes (.onnx), mediapipe.json
#                            (blendshapeInputLandmarks, blendshapeNames), babble-labels.json.
#   FaceDetector.detect(frame)  128 x 128 input [-1, 1] of the frame fitted in a square; 896 anchors (16x16x2, 8x8x6);
#                            sigmoid scores >= 0.5; weighted merge of boxes overlapping the best by IoU >= 0.3.
#   FaceLandmarker.process(frame)  Crop 1.5 x the face, turned level by the eyes (detector keypoints, then landmarks 33
#                            and 263 while tracked); 256 x 256 [0, 1]; presence >= 0.5; 146 landmarks' pixel x, y into the
#                            blendshape model. (landmarks, blendshapes, roll) or None.
#   BabbleModel(model, labels, threads, crop)  Grey / 255 NCHW crop around the mouth (lips and chin centre, 2 x the
#                            cheek distance x crop), turned with the face; outputs named by labels.
#   FaceTracker.track(frame, unified, eye)  expressions.from_arkit, then Babble's values with expressions.set_value.
#   _sample(frame, rect, w, h)  Bilinear crop of a rotated rectangle; black outside the frame.
