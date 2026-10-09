import type { AlertDto } from "@repo/shared/index";

export type AlertRow = AlertDto & { children?: AlertRow[] };

const REPEATED_WITHIN_MINUTE = "RepetidaMinuto";

const timeOf = (alert: AlertDto): number => new Date(alert.endTime ?? alert.startTime ?? 0).getTime();

/**
 * Folds each repeat under the "Nueva" alert it repeats, so the table lists a story once and its
 * repeats can be expanded under it.
 *
 * The link is the alert's own `repeatOfAlertId`, which the worker stores when it classifies the
 * alert, so nothing is guessed from times or text. A repeat whose "Nueva" is not in the list (it
 * is on another day, is filtered out, or the alert was stored before the link existed) stays on
 * its own row. Rows and each group's repeats come back in time order.
 */
export const groupRepeatedAlerts = (alerts: AlertDto[]): AlertRow[] => {
  const rows: AlertRow[] = alerts.map((alert) => ({ ...alert }));
  const rowById = new Map<string, AlertRow>();
  for (const row of rows) {
    if (row.id) rowById.set(row.id, row);
  }

  const topLevel: AlertRow[] = [];
  for (const row of rows) {
    const head = row.repeatOfAlertId ? rowById.get(row.repeatOfAlertId) : undefined;
    if (head && head !== row) {
      (head.children ??= []).push(row);
    } else {
      topLevel.push(row);
    }
  }

  const byTime = (a: AlertDto, b: AlertDto) => timeOf(a) - timeOf(b);
  for (const row of topLevel) {
    row.children?.sort(byTime);
  }
  return topLevel.sort(byTime);
};

/**
 * Drops the "RepetidaMinuto" alerts (the same story again on the same platform within a minute)
 * from grouped rows, both loose and inside a group.
 */
export const hideRepeatedWithinMinute = (rows: AlertRow[]): AlertRow[] =>
  rows
    .filter((row) => row.type !== REPEATED_WITHIN_MINUTE)
    .map((row) => {
      if (!row.children) return row;
      const { children, ...rest } = row;
      const kept = children.filter((child) => child.type !== REPEATED_WITHIN_MINUTE);
      return kept.length > 0 ? { ...rest, children: kept } : rest;
    });
