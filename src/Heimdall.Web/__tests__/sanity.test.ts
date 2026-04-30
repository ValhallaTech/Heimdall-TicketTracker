import { add } from '../scripts/util';

describe('sanity', () => {
  it('confirms Jest matchers work as expected', () => {
    // Arrange
    const left = 2;
    const right = 2;

    // Act
    const result = left + right;

    // Assert
    expect(result).toBe(4);
  });
});

describe('add', () => {
  it('returns the sum of two positive numbers', () => {
    // Arrange
    const a = 3;
    const b = 4;

    // Act
    const result = add(a, b);

    // Assert
    expect(result).toBe(7);
  });

  it('returns the correct result when one operand is negative', () => {
    // Arrange
    const a = 10;
    const b = -3;

    // Act
    const result = add(a, b);

    // Assert
    expect(result).toBe(7);
  });
});
