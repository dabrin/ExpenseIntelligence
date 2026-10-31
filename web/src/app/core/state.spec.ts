import { fmt } from './state';

describe('fmt (es-CR)', () => {
  const norm = (s: string) => s.replace(/[  ]/g, ' ');

  it('formats percentages with one decimal, comma and a space before %', () => {
    expect(norm(fmt.pct(62.5))).toBe('62,5 %');
    expect(norm(fmt.pct(50))).toBe('50,0 %');
  });

  it('formats money with the currency symbol and never throws on unknown codes', () => {
    expect(norm(fmt.money(1234.5, 'CRC'))).toContain('1 234,50');
    expect(fmt.money(10, '???')).toContain('???');
  });

  it('labels months and week ranges in Spanish', () => {
    expect(fmt.period('2026-03', 'month')).toBe('mar 2026');
    expect(fmt.range('2026-03-02', '2026-03-08', 'week')).toBe('2 – 8 mar 2026');
    expect(fmt.range('2026-03-30', '2026-04-05', 'week')).toBe('30 mar – 5 abr 2026');
    expect(fmt.range('2026-03-01', '2026-03-31', 'month')).toBe('mar 2026');
  });
});
