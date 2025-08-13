using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <summary>
    /// Remove People records for Artist-like PersonTypes and migrate them to Artists column.
    /// </summary>
    public partial class RemoveArtistPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Migrate Conductor and Composer names to Artists column before deletion
            migrationBuilder.Sql(@"
                UPDATE BaseItems
                SET Artists = (
                    CASE
                        WHEN Artists IS NULL OR Artists = '' THEN
                            -- No existing artists, just add the new ones
                            (SELECT GROUP_CONCAT(p.Name, '|')
                             FROM (SELECT DISTINCT p.Name
                                   FROM PeopleBaseItemMap pbim
                                   JOIN Peoples p ON pbim.PeopleId = p.Id
                                   WHERE pbim.ItemId = BaseItems.Id
                                     AND p.PersonType IN ('Conductor', 'Composer')
                                   ORDER BY CASE p.PersonType WHEN 'Conductor' THEN 1 WHEN 'Composer' THEN 2 END) p)
                        ELSE
                            -- Merge with existing artists, avoiding duplicates
                            (SELECT CASE
                                WHEN new_names.names IS NULL THEN BaseItems.Artists
                                ELSE BaseItems.Artists || '|' || new_names.names
                            END
                            FROM (
                                SELECT GROUP_CONCAT(p.Name, '|') as names
                                FROM (SELECT DISTINCT p.Name
                                      FROM PeopleBaseItemMap pbim
                                      JOIN Peoples p ON pbim.PeopleId = p.Id
                                      WHERE pbim.ItemId = BaseItems.Id
                                        AND p.PersonType IN ('Conductor', 'Composer')
                                        AND ('|' || BaseItems.Artists || '|') NOT LIKE ('%|' || p.Name || '|%')
                                      ORDER BY CASE p.PersonType WHEN 'Conductor' THEN 1 WHEN 'Composer' THEN 2 END) p
                            ) new_names)
                    END
                )
                WHERE Id IN (
                    SELECT DISTINCT pbim.ItemId
                    FROM PeopleBaseItemMap pbim
                    JOIN Peoples p ON pbim.PeopleId = p.Id
                    WHERE p.PersonType IN ('Conductor', 'Composer')
                );
            ");

            // Step 2: Delete People with removed PersonType values (Artist, AlbumArtist, Composer, Conductor)
            // These are no longer valid PersonKind enum values
            migrationBuilder.Sql(@"
                DELETE FROM Peoples
                WHERE PersonType IN ('Artist', 'AlbumArtist', 'Composer', 'Conductor');
            ");

            // Step 3: Remove PeopleBaseItemMap records that reference deleted People records
            migrationBuilder.Sql(@"
                DELETE FROM PeopleBaseItemMap
                WHERE PeopleId NOT IN (SELECT Id FROM Peoples);
            ");

            // Step 4: Delete orphaned People records (if any were created)
            migrationBuilder.Sql(@"
                DELETE FROM Peoples
                WHERE Id NOT IN (
                    SELECT DISTINCT PeopleId
                    FROM PeopleBaseItemMap
                );
            ");

            // Step 5: Delete orphaned Person BaseItems that don't have corresponding Peoples records
            migrationBuilder.Sql(@"
                DELETE FROM BaseItems
                WHERE Type = 'MediaBrowser.Controller.Entities.Person'
                  AND Name NOT IN (SELECT DISTINCT Name FROM Peoples);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Note: This migration is not easily reversible as we've deleted data.
            // We cannot recreate the original People records or mappings.
        }
    }
}