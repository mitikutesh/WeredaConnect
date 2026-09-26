'use strict';

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

function findUser(fin) {
  return TEST_USERS.find((u) => u.fin === fin) || null;
}

module.exports = { TEST_USERS, findUser };
