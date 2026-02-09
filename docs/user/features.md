# Features

## Complete Feature List

This document provides a comprehensive overview of all WeredaConnect features.

## Audience
- End users
- Product managers
- Stakeholders

---

## Core Features

### 1. Issue Reporting

**Report community problems and track their resolution.**

#### Capabilities
- ✅ Create reports with title, description, photos, and location
- ✅ Categorize issues (Infrastructure, Sanitation, Safety, Health, Education, Other)
- ✅ Add up to 5 photos per report
- ✅ Auto-detect location or manually place pin on map
- ✅ Report anonymously (optional)
- ✅ Edit reports within 24 hours of creation
- ✅ Delete your own reports
- ✅ Track report status (Pending, Acknowledged, In Progress, Resolved, Closed)

#### Offline Support
- ✅ Create reports offline
- ✅ Photos queued for upload when online
- ✅ Automatic sync when connectivity restored

---

### 2. Report Discovery

**Browse and search community reports.**

#### Capabilities
- ✅ View all reports in your wereda
- ✅ Filter by category
- ✅ Filter by status
- ✅ Sort by: Recent, Nearby, Most Upvoted
- ✅ Search reports by keyword
- ✅ View report details (photos, location, comments, status history)
- ✅ View reports on map
- ✅ Share reports (SMS, WhatsApp, social media)

#### Offline Support
- ✅ View previously loaded reports offline
- ✅ Cached photos available offline
- ❌ Search requires internet (local cache search coming soon)

---

### 3. Engagement

**Interact with reports and community members.**

#### Capabilities
- ✅ Upvote reports (show support)
- ✅ Comment on reports
- ✅ Reply to comments
- ✅ Follow reports (get notifications on updates)
- ✅ Share reports with others
- ✅ Thank report creators

#### Offline Support
- ✅ Upvote/comment offline (syncs later)
- ✅ View existing comments offline
- ❌ Real-time comment updates require internet

---

### 4. Community Discussions

**Engage in community conversations.**

#### Capabilities
- ✅ Create discussion topics
- ✅ Post in discussions
- ✅ Reply to posts
- ✅ Upvote/downvote posts
- ✅ Follow topics
- ✅ Browse by category (Events, Ideas, Questions, Announcements)
- ✅ Search discussions

#### Offline Support
- ✅ View previously loaded discussions offline
- ✅ Create posts offline (sync later)
- ❌ Real-time updates require internet

---

### 5. Notifications

**Stay informed about community activity.**

#### Notification Types
- 🔔 Report status changes
- 🔔 Comments on your reports
- 🔔 Replies to your comments
- 🔔 Upvotes on your content
- 🔔 New reports in followed categories
- 🔔 Community announcements
- 🔔 Emergency alerts

#### Settings
- ✅ Enable/disable by type
- ✅ Quiet hours (no notifications at night)
- ✅ Sound/vibration preferences
- ✅ In-app vs push notifications

---

### 6. User Profile

**Manage your account and preferences.**

#### Profile Information
- ✅ Name, phone number, email
- ✅ Wereda (district)
- ✅ Profile photo
- ✅ Bio (optional)
- ✅ Preferred language

#### Activity
- ✅ View your reports
- ✅ View your comments
- ✅ View your discussions
- ✅ View upvoted content
- ✅ View followed topics

#### Settings
- ✅ Change language (Amharic, Afan Oromo, English)
- ✅ Notification preferences
- ✅ Privacy settings
- ✅ Data saver mode
- ✅ Sync preferences (WiFi only, auto-sync)
- ✅ Dark mode / Light mode
- ✅ Delete account

---

### 7. Offline-First Architecture

**Work seamlessly with or without internet.**

#### Capabilities
- ✅ Local database (Isar) stores all data
- ✅ Automatic background sync
- ✅ Sync queue for pending operations
- ✅ Conflict resolution (server wins, client merges)
- ✅ Sync status indicator
- ✅ Manual sync trigger
- ✅ View sync history

#### Data Management
- ✅ Cache reports for offline viewing
- ✅ Cache photos (configurable size limit)
- ✅ Clear cache to free space
- ✅ Export your data

---

### 8. Localization

**Support for Ethiopian languages.**

#### Supported Languages
- 🇪🇹 አማርኛ (Amharic)
- 🇪🇹 Afaan Oromoo (Afan Oromo)
- 🌍 English

#### Localized Content
- ✅ UI text
- ✅ Error messages
- ✅ Notifications
- ✅ Help documentation
- ✅ Date/time formats
- ✅ Number formats

---

### 9. Maps & Location

**Visualize reports geographically.**

#### Capabilities
- ✅ View reports on map
- ✅ Cluster nearby reports
- ✅ Filter map by category
- ✅ Auto-detect current location
- ✅ Manually place report location
- ✅ View report location details
- ✅ Get directions to report location

#### Offline Support
- ✅ Cached map tiles for offline viewing
- ✅ GPS works offline
- ❌ Directions require internet

---

### 10. Media Management

**Handle photos efficiently.**

#### Capabilities
- ✅ Take photos in-app
- ✅ Select from gallery
- ✅ Crop/rotate photos
- ✅ Compress photos (save data)
- ✅ View full-screen photos
- ✅ Zoom/pan photos
- ✅ Download photos

#### Offline Support
- ✅ Photos cached for offline viewing
- ✅ Upload queued when offline
- ✅ Retry failed uploads

---

## Upcoming Features

### Phase 2 (Q2 2026)
- 📅 Event organization
- 📊 Analytics dashboard for community leaders
- 🏆 Gamification (badges, leaderboards)
- 🔍 Advanced search filters
- 📱 SMS integration (for feature phones)

### Phase 3 (Q3 2026)
- 🗳️ Polls and surveys
- 💰 Budget transparency
- 📈 Progress tracking for government projects
- 🤝 Volunteer coordination
- 🎓 Educational resources

### Phase 4 (Q4 2026)
- 🌐 Multi-wereda support
- 🔗 Integration with government systems
- 📞 Voice reporting (for accessibility)
- 🤖 AI-powered issue categorization
- 📊 Data export for researchers

---

## Feature Comparison

### Free vs Premium (Future)

| Feature | Free | Premium |
|---------|------|---------|
| Create reports | ✅ Unlimited | ✅ Unlimited |
| View reports | ✅ Unlimited | ✅ Unlimited |
| Comments | ✅ Unlimited | ✅ Unlimited |
| Discussions | ✅ Unlimited | ✅ Unlimited |
| Photos per report | ✅ 5 | ✅ 10 |
| Offline storage | ✅ 100 MB | ✅ 500 MB |
| Analytics | ❌ | ✅ |
| Priority support | ❌ | ✅ |
| Ad-free | ❌ | ✅ |

**Note**: Premium features are planned for sustainability but not yet implemented.

---

## Technical Features

### For Developers

See **[Developer Documentation](../developer/)** for:
- API endpoints
- Data models
- Sync protocols
- Testing guidelines

---

## Accessibility Features

### Current
- ✅ Large text support
- ✅ High contrast mode
- ✅ Screen reader support (basic)
- ✅ Simple, clear UI

### Planned
- 📅 Voice commands
- 📅 Audio descriptions
- 📅 Simplified mode for low literacy
- 📅 Offline voice reporting

---

## Related Documentation

- **[User Guide](./user-guide.md)** - How to use WeredaConnect
- **[Troubleshooting](./troubleshooting.md)** - Common issues and solutions

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Product Owner**: Product Team

