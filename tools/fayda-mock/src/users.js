'use strict';

const crypto = require('node:crypto');

// Seeded test identities. FINs are 16 digits like real Fayda FINs.
// The WeredaConnect server decides what each identity means (for example,
// 4444... is used for the "already linked" scenario). This mock only issues
// the claims.
const TEST_USERS = [
  { fin: '1111111111111111', name: 'Abebe Kebede', givenName: 'Abebe', familyName: 'Kebede', phone: '+251900100001', hint: 'farmer' },
  { fin: '2222222222222222', name: 'Chaltu Tolosa', givenName: 'Chaltu', familyName: 'Tolosa', phone: '+251900100002', hint: 'trucker' },
  { fin: '3333333333333333', name: 'Sara Haile', givenName: 'Sara', familyName: 'Haile', phone: '+251900100003', hint: 'buyer' },
  { fin: '4444444444444444', name: 'Linked User', givenName: 'Linked', familyName: 'User', phone: '+251900100004', hint: 'already linked' },
  { fin: '5555555555555555', name: 'No Phone', givenName: 'No', familyName: 'Phone', phone: null, hint: 'no phone_number claim' },
];

// Any other 16-digit FIN is a synthetic identity. Its name is picked from
// these lists by a hash of the FIN, so the same FIN always gets the same name.
const GIVEN_NAMES = ['Almaz', 'Bekele', 'Dawit', 'Genet', 'Hana', 'Kedir', 'Lensa', 'Meron',
  'Mulugeta', 'Obsa', 'Rahel', 'Selam', 'Tadesse', 'Tigist', 'Worku', 'Yonas'];
const FAMILY_NAMES = ['Abdi', 'Alemu', 'Bekele', 'Desta', 'Gebre', 'Girma', 'Hailu', 'Jemal',
  'Kassa', 'Mekonnen', 'Negash', 'Regassa', 'Tesfaye', 'Wolde', 'Yimer', 'Zewde'];

const isValidFin = (fin) => typeof fin === 'string' && /^\d{16}$/.test(fin);

/**
 * Synthetic identity for a FIN outside the seeded list. It carries no phone,
 * so the server cannot treat the phone as Fayda-proven and asks for the SMS OTP.
 */
function syntheticUser(fin) {
  const h = crypto.createHash('sha256').update(`fayda-mock-name:${fin}`).digest();
  const givenName = GIVEN_NAMES[h[0] % GIVEN_NAMES.length];
  const familyName = FAMILY_NAMES[h[1] % FAMILY_NAMES.length];
  return { fin, name: `${givenName} ${familyName}`, givenName, familyName, phone: null, hint: 'synthetic', synthetic: true };
}

/** Seeded user for a known FIN, synthetic user for any other 16-digit FIN, else null. */
function findUser(fin) {
  if (!isValidFin(fin)) return null;
  return TEST_USERS.find((u) => u.fin === fin) || syntheticUser(fin);
}

/** Random 16-digit FIN (no leading zero) that is not one of the seeded ones. */
function generateFin() {
  for (;;) {
    // randomInt caps at 2^48, so draw the 16 digits as 1 + 7 + 8.
    const fin = String(crypto.randomInt(1, 10))
      + String(crypto.randomInt(0, 1e7)).padStart(7, '0')
      + String(crypto.randomInt(0, 1e8)).padStart(8, '0');
    if (!TEST_USERS.some((u) => u.fin === fin)) return fin;
  }
}

module.exports = { TEST_USERS, findUser, isValidFin, generateFin };
