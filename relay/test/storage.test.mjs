import { test } from "node:test";
import assert from "node:assert/strict";
import { keptNotes } from "../src/notes.js";

function fakeSql(hasTable, rows = []) {
  const calls = [];
  return {
    calls,
    exec(sql, ...args) {
      calls.push({ sql, args });
      if (sql.includes("sqlite_master")) return { toArray: () => hasTable ? [{ 1: 1 }] : [] };
      if (sql.startsWith("SELECT id, time, message FROM notes")) return { toArray: () => rows };
      throw new Error(`unexpected storage write or query: ${sql}`);
    },
  };
}

test("a GET-only inbox does not create a notes table or schedule cleanup", () => {
  const sql = fakeSql(false);
  assert.deepEqual(keptNotes(sql, 0, 1_700_000_000), []); // since=all, as both polling and follow use
  assert.equal(sql.calls.length, 1);
  assert.match(sql.calls[0].sql, /sqlite_master/);
  assert.doesNotMatch(sql.calls[0].sql, /CREATE|INSERT|UPDATE|DELETE/i);
});

test("following without since does not even inspect SQLite", () => {
  const sql = fakeSql(false);
  assert.deepEqual(keptNotes(sql, null, 1_700_000_000), []);
  assert.deepEqual(sql.calls, []);
});

test("an existing notes table is read without initializing or modifying it", () => {
  const rows = [{ id: "a", time: 100, message: "sealed" }];
  const sql = fakeSql(true, rows);
  assert.deepEqual(keptNotes(sql, 50, 200), rows);
  assert.equal(sql.calls.length, 2);
  assert.match(sql.calls[0].sql, /sqlite_master/);
  assert.match(sql.calls[1].sql, /^SELECT/);
  assert.deepEqual(sql.calls[1].args, [50, 200]);
});
