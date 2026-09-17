import { describe, it, expect } from 'vitest';
import { detectTextLanguage } from './i18n';

describe('detectTextLanguage', () => {
  it('detects Arabic', () => {
    expect(detectTextLanguage('يجب ألا يتحرك هذا الزر عند تحميل الصفحة من فضلك أصلحوا هذا الخلل')).toBe('ar');
  });

  it('detects Persian via a Persian-only letter (گ)', () => {
    expect(detectTextLanguage('این دکمه گزارش خطا را نشان می دهد و باید درست کار کند لطفا بررسی کنید')).toBe('fa');
  });

  it('detects Urdu via an Urdu-only letter (ٹ)', () => {
    expect(detectTextLanguage('یہ بٹن ٹھیک کام نہیں کر رہا براہ کرم اسے درست کریں شکریہ')).toBe('ur');
  });

  it('detects Pashto via a Pashto-only letter (ږ)', () => {
    expect(detectTextLanguage('دا تڼۍ سمه نه ده کارول کیږي مهرباني وکړئ دا سمه کړئ')).toBe('ps');
  });

  it('detects Hebrew', () => {
    expect(detectTextLanguage('הכפתור הזה לא עובד כמו שצריך ואני חושב שיש כאן תקלה קטנה בקוד')).toBe('he');
  });

  it('detects Japanese via kana', () => {
    expect(detectTextLanguage('このボタンはページの読み込み時に動いてはいけませんこれはバグですお願いします')).toBe('ja');
  });

  it('detects Korean via hangul', () => {
    expect(detectTextLanguage('이 버튼은 페이지가 로드될 때 움직이면 안 됩니다 이것을 수정해 주세요')).toBe('ko');
  });

  it('detects Chinese (Han with no kana)', () => {
    expect(detectTextLanguage('这个按钮在页面加载时不应该移动这是一个错误请修复它谢谢大家')).toBe('zh');
  });

  it('detects Ukrainian via a Ukrainian-only letter (ї)', () => {
    expect(detectTextLanguage('Ця кнопка не повинна рухатися під час завантаження сторінки це помилка їжак')).toBe('uk');
  });

  it('detects English via >=3 stopwords and pure ASCII', () => {
    expect(detectTextLanguage('the button should not move when the page loads please fix this')).toBe('en');
  });

  it('returns unknown for French text with diacritics even with English-like word count', () => {
    expect(detectTextLanguage('Le bouton ne doit pas bouger lorsque la page se charge, corrigez ceci s\'il vous plaît')).toBe('unknown');
  });

  it('returns unknown for short text', () => {
    expect(detectTextLanguage('fix this')).toBe('unknown');
  });

  it('returns unknown for Arabic-script text using only the ambiguous Persian-keyboard letters (ک/ی)', () => {
    // Enough letters, Arabic script, but the only "special" chars are ک/ی which Arabic
    // typists/fonts also produce — not enough to call it Persian.
    expect(detectTextLanguage('یک یک یک یک یک یک یک یک یک یک یک یک یک یک یک')).toBe('unknown');
  });

  it('returns unknown for plain Cyrillic with no Ukrainian-only letters', () => {
    expect(detectTextLanguage('Эта кнопка не должна двигаться при загрузке страницы пожалуйста исправьте это')).toBe('unknown');
  });

  it('returns unknown for Latin text with fewer than 3 stopwords', () => {
    expect(detectTextLanguage('lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod')).toBe('unknown');
  });
  it('keeps English when only punctuation is non-ASCII (smart quotes, em dash)', () => {
    expect(detectTextLanguage('The “Save” button should not close the page — please fix this when you can')).toBe('en');
  });
});
