"""The server's settings: these defaults, overridden by an INI file such as
server/deploy/server.conf."""

import configparser
import dataclasses

MIB = 1024 * 1024


@dataclasses.dataclass
class Config:
    host: str = "127.0.0.1"
    port: int = 8765
    data_dir: str = "/var/lib/t7drop"
    max_item_bytes: int = 200 * MIB
    max_total_bytes: int = 2048 * MIB
    max_meta_bytes: int = 4096
    max_items_per_device: int = 200
    unfinished_hours: float = 24
    undelivered_days: float = 7
    delivered_days: float = 7
    max_wait_seconds: int = 55
    max_failed_pairings: int = 10  # an hour, all devices together


_SUFFIXES = {"K": 1024, "M": MIB, "G": 1024 * MIB}


def parse_size(text):
    """A size in bytes, such as 4096, 200M or 2G."""
    text = text.strip().upper()
    if text[-1:] in _SUFFIXES:
        return int(float(text[:-1]) * _SUFFIXES[text[-1]])
    return int(text)


def load(path=None):
    """Reads [server] listen and data_dir, and any field of Config under [limits]."""
    config = Config()
    if path is None:
        return config
    parser = configparser.ConfigParser()
    with open(path, encoding="utf-8") as f:
        parser.read_file(f)
    if parser.has_option("server", "listen"):
        host, _, port = parser.get("server", "listen").rpartition(":")
        config.host, config.port = host, int(port)
    if parser.has_option("server", "data_dir"):
        config.data_dir = parser.get("server", "data_dir")
    for field in dataclasses.fields(Config):
        if parser.has_option("limits", field.name):
            raw = parser.get("limits", field.name)
            value = parse_size(raw) if field.name.endswith("_bytes") else field.type(raw)
            setattr(config, field.name, value)
    return config
