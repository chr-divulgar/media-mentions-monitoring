// Self-check of excelImport.ts with the real headers of "Ecopetrol OCTUBRE 2026.xlsx".
// Run: node apps/web-ui/src/pages/notes/excelImport.check.ts
import assert from "node:assert/strict";
import { excelSerialToIsoDate, mapHeaders, missingRequired } from "./excelImport.ts";

// A dated sheet ("Oct 26 a nov 01"): renamed columns and a typo, and no "Ciudad".
const weekly = mapHeaders(["Feha", "Tipo de medio", "Medio", "VARIABLES", "Tema", "Sub-temas", "Origen", "Departamento",
  "Zona", "Título", "RESUMEN", "Tarifa", "Sentimiento", "VALOR", "Audiencia", "Link", ""]);
assert.deepEqual(missingRequired(weekly), []);
assert.equal(weekly.date, 0);
assert.equal(weekly.title, 9);
assert.equal(weekly.subsubtopic, 5);
assert.equal(weekly.link, 15);
assert.equal(weekly.city, undefined);

// The old names still work.
assert.deepEqual(missingRequired(mapHeaders(["Fecha", "TIPO DE MEDIO", "Medio", "Titular", "Ciudad"])), []);

// A pivot table sheet ("1. Publi Audie Sentim") is not importable, and says why.
assert.deepEqual(missingRequired(mapHeaders(["Origen", "Directa", ""])), ["Fecha", "Tipo de medio", "Medio", "Titular"]);

// Excel serials: 46321 is 2026-10-26 whatever the machine's time zone.
assert.equal(excelSerialToIsoDate(46321), "2026-10-26");
assert.equal(excelSerialToIsoDate(46321.75), "2026-10-26");

console.log("excelImport: ok");
