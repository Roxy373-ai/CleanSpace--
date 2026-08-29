from pathlib import Path

from PIL import Image

from cleanspace.media import check_image, difference_hash, find_similar_images, hamming_distance
from cleanspace.models import MediaCheckResult, MediaState


def test_image_validation_and_hash(tmp_path):
    image_path = tmp_path / "测试.png"
    Image.new("RGB", (32, 32), (120, 40, 200)).save(image_path)
    result = check_image(image_path)
    assert result.state is MediaState.VALID
    assert result.perceptual_hash == difference_hash(image_path)


def test_broken_image_is_detected(tmp_path):
    path = tmp_path / "broken.jpg"
    path.write_bytes(b"not an image")
    assert check_image(path).state is MediaState.BROKEN


def test_similar_hashes_are_candidates_only(tmp_path):
    left = MediaCheckResult(tmp_path / "a.png", MediaState.VALID, perceptual_hash="0000000000000000")
    right = MediaCheckResult(tmp_path / "b.png", MediaState.VALID, perceptual_hash="0000000000000001")
    assert hamming_distance(left.perceptual_hash, right.perceptual_hash) == 1
    assert find_similar_images([left, right], threshold=2) == [[left.path, right.path]]

