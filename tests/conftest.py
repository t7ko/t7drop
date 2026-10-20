import pytest

import helpers


@pytest.fixture
def clock():
    return helpers.Clock()


@pytest.fixture
def start_server(tmp_path, clock):
    servers = []

    def start(port=0, **settings):
        server = helpers.start(tmp_path, clock, port, **settings)
        servers.append(server)
        return server

    yield start
    for server in servers:
        server.shutdown()


@pytest.fixture
def server(start_server):
    return start_server()
