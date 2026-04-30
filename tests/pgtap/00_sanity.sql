-- 00_sanity.sql
-- Baseline pgTAP sanity check. Verifies the pgtap extension is loaded and
-- that the test harness can execute basic assertions. This file is wrapped
-- in a transaction and rolled back so it leaves no residue in the database.

BEGIN;

SELECT plan(2);

SELECT has_extension('pgtap');
SELECT ok(1 + 1 = 2, 'arithmetic works');

SELECT * FROM finish();

ROLLBACK;
