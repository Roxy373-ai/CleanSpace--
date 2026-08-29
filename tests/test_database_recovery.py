from cleanspace.app import open_database_with_recovery


def test_corrupt_index_is_preserved_and_rebuilt(tmp_path):
    database_path = tmp_path / "cleanspace.db"
    database_path.write_bytes(b"not a sqlite database")
    database, backup = open_database_with_recovery(database_path)
    assert backup is not None
    assert (backup / "cleanspace.db").read_bytes() == b"not a sqlite database"
    assert database.full_integrity_check() == "ok"
