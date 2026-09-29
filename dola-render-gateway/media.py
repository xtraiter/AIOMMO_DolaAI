"""Public reference media download and security validation (SSRF protected)."""
import asyncio
import ipaddress
import shutil
import socket
import tempfile
from io import BytesIO
from pathlib import Path
from urllib.parse import urljoin, urlparse

import aiohttp
from PIL import Image

import config

_ALLOWED_IMAGE_FORMATS = {"JPEG": ".jpg", "PNG": ".png", "WEBP": ".webp"}


def validate_public_url(url: str) -> str:
    """Accepts only public HTTP(S) URLs, blocks localhost, private network, and credentials."""
    if not isinstance(url, str) or len(url) > 4096:
        raise ValueError("Invalid reference image URL")
    parsed = urlparse(url)
    if parsed.scheme.lower() not in {"http", "https"} or not parsed.hostname:
        raise ValueError("Reference image only supports public http/https URLs")
    if parsed.username or parsed.password:
        raise ValueError("Reference image URL must not contain authentication credentials")
    host = parsed.hostname
    try:
        # SSRF check: check direct IP and DNS resolution against private/reserved ranges
        addresses = {ipaddress.ip_address(host)}
    except ValueError:
        try:
            infos = awaitable_getaddrinfo(host)
            addresses = {ipaddress.ip_address(x) for x in infos}
        except Exception as exc:
            raise ValueError(f"Failed to resolve reference image domain: {host}") from exc
    if not addresses or any(
        ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved
        or ip.is_multicast or ip.is_unspecified
        for ip in addresses
    ):
        raise ValueError("Reference image points to private or reserved IP address (SSRF blocked)")
    return url


def awaitable_getaddrinfo(host: str) -> list[str]:
    """Synchronous DNS resolution wrapper."""
    return [item[4][0] for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)]


async def _validate_url_async(url: str) -> str:
    if not isinstance(url, str) or len(url) > 4096:
        raise ValueError("Invalid reference image URL")
    parsed = urlparse(url)
    if parsed.scheme.lower() not in {"http", "https"} or not parsed.hostname:
        raise ValueError("Reference image only supports public http/https URLs")
    if parsed.username or parsed.password:
        raise ValueError("Reference image URL must not contain authentication credentials")
    host = parsed.hostname
    try:
        addresses = {ipaddress.ip_address(host)}
    except ValueError:
        try:
            resolved = await asyncio.to_thread(awaitable_getaddrinfo, host)
            addresses = {ipaddress.ip_address(x) for x in resolved}
        except Exception as exc:
            raise ValueError(f"Failed to resolve reference image domain: {host}") from exc
    if not addresses or any(
        ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved
        or ip.is_multicast or ip.is_unspecified
        for ip in addresses
    ):
        raise ValueError("Reference image points to private or reserved IP address (SSRF blocked)")
    return url


async def validate_reference_urls(urls: list[str]) -> list[str]:
    if len(urls) > config.REFERENCE_IMAGE_MAX_COUNT:
        raise ValueError(f"Maximum of {config.REFERENCE_IMAGE_MAX_COUNT} reference images allowed")
    normalized = []
    seen = set()
    for raw in urls:
        url = await _validate_url_async(raw)
        if url not in seen:
            normalized.append(url)
            seen.add(url)
    return normalized


async def _read_response_image(resp: aiohttp.ClientResponse) -> tuple[bytes, str]:
    content_length = resp.headers.get("Content-Length")
    if content_length and int(content_length) > config.REFERENCE_IMAGE_MAX_BYTES:
        raise ValueError("Reference image exceeds single file size limit")
    chunks = []
    total = 0
    async for chunk in resp.content.iter_chunked(64 * 1024):
        total += len(chunk)
        if total > config.REFERENCE_IMAGE_MAX_BYTES:
            raise ValueError("Reference image exceeds single file size limit")
        chunks.append(chunk)
    data = b"".join(chunks)
    try:
        with Image.open(BytesIO(data)) as image:
            image.verify()
            fmt = image.format
    except Exception as exc:
        raise ValueError("Reference file is not a valid image") from exc
    if fmt not in _ALLOWED_IMAGE_FORMATS:
        raise ValueError("Reference image only supports JPEG, PNG, WEBP")
    return data, _ALLOWED_IMAGE_FORMATS[fmt]


async def download_one_image(session: aiohttp.ClientSession, url: str, dest: Path) -> Path:
    current = await _validate_url_async(url)
    # Try configured proxy first, fall back to direct connection if blocked
    proxies = []
    if config.PROXY:
        proxies.append(config.PROXY)
    proxies.append(None)
    last_error = None
    for proxy in proxies:
        current = await _validate_url_async(url)
        for _ in range(5):
            try:
                async with session.get(
                    current,
                    allow_redirects=False,
                    proxy=proxy,
                    timeout=aiohttp.ClientTimeout(total=config.REFERENCE_DOWNLOAD_TIMEOUT),
                    headers={"User-Agent": "dola-pool-reference-fetch/1.0"},
                ) as resp:
                    if 300 <= resp.status < 400 and resp.headers.get("Location"):
                        current = await _validate_url_async(urljoin(current, resp.headers["Location"]))
                        continue
                    if resp.status != 200:
                        raise ValueError(f"Failed to download reference image: HTTP {resp.status}")
                    data, suffix = await _read_response_image(resp)
                    path = dest.with_suffix(suffix)
                    path.write_bytes(data)
                    return path
            except Exception as exc:
                last_error = exc
                break
    raise ValueError(str(last_error) if last_error else "Failed to download reference image")


def validate_local_image_paths(paths: list[str]) -> list[str]:
    """Local reference images picked by DolaCoordinator (same machine). Must be absolute paths to real images.

    Only ever called for requests from the loopback interface (see server.create_video).
    """
    if len(paths) > config.REFERENCE_IMAGE_MAX_COUNT:
        raise ValueError(f"Maximum of {config.REFERENCE_IMAGE_MAX_COUNT} reference images allowed")
    result, seen = [], set()
    for raw in paths:
        path = Path(raw)
        if not path.is_absolute() or not path.is_file():
            raise ValueError(f"Reference image not found: {raw}")
        if path.stat().st_size > config.REFERENCE_IMAGE_MAX_BYTES:
            raise ValueError(f"Reference image exceeds single file size limit: {path.name}")
        try:
            with Image.open(path) as image:
                image.verify()
                fmt = image.format
        except Exception as exc:
            raise ValueError(f"Reference file is not a valid image: {path.name}") from exc
        if fmt not in _ALLOWED_IMAGE_FORMATS:
            raise ValueError(f"Reference image only supports JPEG, PNG, WEBP: {path.name}")
        key = str(path.resolve())
        if key not in seen:
            result.append(key)
            seen.add(key)
    return result


def copy_local_reference_images(paths: list[str], task_id: str) -> tuple[Path | None, list[str]]:
    """Copies validated local images into a temp folder (sanitised names). Caller must remove the folder."""
    validated = validate_local_image_paths(paths)
    if not validated:
        return None, []
    root = Path(tempfile.mkdtemp(prefix=f"dola_local_{task_id}_"))
    out = []
    try:
        for index, src in enumerate(validated):
            with Image.open(src) as image:
                suffix = _ALLOWED_IMAGE_FORMATS[image.format]
            dest = root / f"local_{index}{suffix}"
            shutil.copyfile(src, dest)
            out.append(str(dest))
        return root, out
    except Exception:
        shutil.rmtree(root, ignore_errors=True)
        raise


async def download_reference_images(urls: list[str], task_id: str) -> tuple[Path | None, list[str]]:
    """Downloads reference images to temp folder, returns (root_dir, local_paths). Caller must cleanup."""
    urls = await validate_reference_urls(urls)
    if not urls:
        return None, []
    root = Path(tempfile.mkdtemp(prefix=f"dola_ref_{task_id}_"))
    try:
        async with aiohttp.ClientSession() as session:
            paths = []
            for index, url in enumerate(urls):
                paths.append(str(await download_one_image(session, url, root / f"image_{index}")))
        return root, paths
    except Exception:
        for child in root.glob("*"):
            child.unlink(missing_ok=True)
        root.rmdir()
        raise